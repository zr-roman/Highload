using System.Diagnostics;
using System.Text.Json;
using Npgsql;

namespace Matrix;

/// <summary>
/// The table payments for the measurements of the search for stale payments (docs/load-testing.md, «Индекс для
/// поиска зависших»): --prefill fills it with captured payments, as on a stand that has worked for days, and
/// --stale-index puts the partial index of the first measurement there or takes it away. After each repeat it tells
/// what the search of PaymentRecovery cost and how many status changes stayed HOT updates.
/// </summary>
/// <remarks>
/// The connection string of Payments.Api carries statement_timeout=5s: what the service may wait for. A fill
/// of millions of rows and a search through them must not stop there, so every command here lifts it.
/// </remarks>
public sealed class PaymentsTable(NpgsqlDataSource payments)
{
    // Measured and rejected: it makes the status change a non-HOT update. The option stays to drop the index left
    // by that measurement (--fresh keeps indexes) and to repeat it.
    public const string StaleIndex = "payments_stale_created";

    // The search of PaymentStore.FindStaleCreatedAsync with the values of Recovery in appsettings.json: payments
    // older than 30 s, 100 at a time, from the queue pending_payments.
    private const string StaleSearch = """
        SELECT p.id, p.merchant_id, p.idempotency_key, p.request_hash, p.amount, p.currency, p.fee, p.status, p.created_at
        FROM pending_payments q JOIN payments p ON p.id = q.id
        WHERE q.created_at < now() - interval '30 seconds' AND p.status = 'created'
        ORDER BY q.created_at LIMIT 100
        """;

    private const int PrefillChunk = 500_000;

    /// <summary>
    /// Captured payments of the 50 merchants of k6, a day old and older. Their ids are 00000000-0000-0000-0000-…
    /// with the row number: in order, so the primary key grows at its end, and not to be taken for real ones.
    /// They are only in payments: not in pending_payments, as captured ones, and the ledger and its /invariants never
    /// see them.
    /// </summary>
    public async Task PrefillAsync(int rows, CancellationToken ct)
    {
        var took = Stopwatch.StartNew();
        await using var connection = await OpenAsync(ct);

        for (var from = 1; from <= rows; from += PrefillChunk)
        {
            var to = Math.Min(rows, from + PrefillChunk - 1);
            await using var insert = new NpgsqlCommand("""
                INSERT INTO payments (id, merchant_id, idempotency_key, request_hash, amount, currency, fee, status, created_at, updated_at)
                SELECT ('00000000-0000-0000-0000-' || lpad(to_hex(g), 12, '0'))::uuid, 1 + g % 50, 'prefill-' || g, 'prefill',
                       150000, 'RUB', 1500, 'captured', now() - interval '1 day' - g * interval '10 milliseconds', now() - interval '1 day'
                FROM generate_series($1::bigint, $2::bigint) AS g
                """, connection) { CommandTimeout = 0 };
            insert.Parameters.Add(new NpgsqlParameter<long> { TypedValue = from });
            insert.Parameters.Add(new NpgsqlParameter<long> { TypedValue = to });
            await insert.ExecuteNonQueryAsync(ct);

            Console.WriteLine($"  Postgres: {to:N0} of {rows:N0} payments written in advance");
        }

        // As autovacuum would have left a table of days: the visibility map set, the statistics fresh. Otherwise
        // autovacuum and autoanalyze would start on it in the middle of the first repeats.
        await using (var vacuum = new NpgsqlCommand("VACUUM (ANALYZE) payments", connection) { CommandTimeout = 0 })
            await vacuum.ExecuteNonQueryAsync(ct);

        Console.WriteLine($"  Postgres: payments filled and vacuumed in {took.Elapsed.TotalSeconds:0} s");
    }

    /// <summary>
    /// Puts the partial index of the stale payments into payments, or drops it. Without --stale-index the matrix
    /// leaves the table as it is: --fresh empties the tables but keeps their indexes.
    /// </summary>
    public async Task SetStaleIndexAsync(bool on, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        var sql = on
            ? $"CREATE INDEX IF NOT EXISTS {StaleIndex} ON payments (created_at) WHERE status = 'created'"
            : $"DROP INDEX IF EXISTS {StaleIndex}";

        await using (var command = new NpgsqlCommand(sql, connection) { CommandTimeout = 0 })
            await command.ExecuteNonQueryAsync(ct);

        Console.WriteLine($"  Postgres: the index {StaleIndex} {(on ? "is there" : "is not there")}");
    }

    /// <summary>
    /// Updates of payments so far and how many of them were HOT. The counters reach pg_stat_user_tables from each
    /// backend within a second or so of its transaction, and from an idle one after up to 10 s: a repeat's
    /// count may take a few updates of the one before.
    /// </summary>
    public async Task<UpdateCounts> ReadUpdatesAsync(CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = new NpgsqlCommand(
            "SELECT n_tup_upd, n_tup_hot_upd FROM pg_stat_user_tables WHERE relname = 'payments'", connection);
        await using var reader = await command.ExecuteReaderAsync(ct);

        return await reader.ReadAsync(ct) ? new UpdateCounts(reader.GetInt64(0), reader.GetInt64(1)) : new UpdateCounts(0, 0);
    }

    /// <summary>The search of PaymentRecovery once, with EXPLAIN ANALYZE: how long it took and how it read payments.</summary>
    public async Task<StaleSearchPlan> ExplainStaleSearchAsync(CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = new NpgsqlCommand($"EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON) {StaleSearch}", connection) { CommandTimeout = 0 };
        var json = (string)(await command.ExecuteScalarAsync(ct))!;

        using var document = JsonDocument.Parse(json);
        var top = document.RootElement[0];
        var plan = top.GetProperty("Plan");

        return new StaleSearchPlan(
            Milliseconds: top.GetProperty("Execution Time").GetDouble(),
            Scan: ScanOfPayments(plan) ?? plan.GetProperty("Node Type").GetString()!,
            Pages: plan.GetProperty("Shared Hit Blocks").GetInt64() + plan.GetProperty("Shared Read Blocks").GetInt64());
    }

    // The node that reads payments: Seq Scan without the index, Index Scan with it.
    private static string? ScanOfPayments(JsonElement node)
    {
        if (node.TryGetProperty("Relation Name", out var relation) && relation.GetString() == "payments")
            return node.GetProperty("Node Type").GetString();

        if (node.TryGetProperty("Plans", out var children))
            foreach (var child in children.EnumerateArray())
                if (ScanOfPayments(child) is { } scan)
                    return scan;

        return null;
    }

    private async Task<NpgsqlConnection> OpenAsync(CancellationToken ct)
    {
        var connection = await payments.OpenConnectionAsync(ct);
        await using var unlimited = new NpgsqlCommand("SET statement_timeout = 0", connection);
        await unlimited.ExecuteNonQueryAsync(ct);
        return connection;
    }
}

public readonly record struct UpdateCounts(long Updates, long Hot)
{
    /// <summary>The share of HOT updates between two readings; null if there were no updates.</summary>
    public static double? HotShare(UpdateCounts before, UpdateCounts after) =>
        after.Updates > before.Updates ? (double)(after.Hot - before.Hot) / (after.Updates - before.Updates) : null;
}

/// <summary>One search of stale payments: its time, the node that read payments, the pages it touched (8 KB each).</summary>
public sealed record StaleSearchPlan(double Milliseconds, string Scan, long Pages);
