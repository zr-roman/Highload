using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using EndToEnd.Tests.Infrastructure;
using Ledger.Service.Ledger;
using Npgsql;
using Payments.Api.Payments;
using Payments.Api.Tests.Infrastructure;

namespace EndToEnd.Tests;

/// <summary>
/// The README's guarantees, checked through the whole platform under concurrent load: no payment is charged twice,
/// no captured payment is lost or booked twice on its way through the outbox and Kafka, and the money in
/// Payments.Api and in Ledger.Service adds up.
/// </summary>
public sealed class MoneyAddsUpTests(PlatformFixture platform, ITestOutputHelper output) : IClassFixture<PlatformFixture>
{
    private const int Orders = 2000;
    private const int Merchants = 50;
    private const int DuplicatedOrders = 20;
    private const int CopiesOfDuplicate = 5;
    private const int Concurrency = 16;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Money_Adds_Up_Across_Services_Under_Concurrent_Load()
    {
        var random = new Random(2026);   // the same merchants and amounts on every run
        var orders = Enumerable.Range(0, Orders).Select(_ => NewOrder(random, copies: 1))
            .Concat(Enumerable.Range(0, DuplicatedOrders).Select(_ => NewOrder(random, copies: CopiesOfDuplicate)))
            .OrderBy(_ => random.Next())
            .ToList();

        // 1. Load: 16 orders at a time. The copies of a duplicated order are sent all at once,
        //    the way a client retries when its first request seems to hang.
        var answers = new ConcurrentBag<(Order Order, HttpStatusCode Status, PaymentResponse Payment)>();
        var parallel = new ParallelOptions { MaxDegreeOfParallelism = Concurrency, CancellationToken = Ct };
        await Parallel.ForEachAsync(orders, parallel, async (order, ct) =>
        {
            var copies = await Task.WhenAll(Enumerable.Range(0, order.Copies).Select(_ => PayAsync(order, ct)));
            foreach (var (status, payment) in copies)
                answers.Add((order, status, payment));
        });

        // 2. No double charge: every order, however many copies, is one payment.
        foreach (var copies in answers.GroupBy(a => a.Order.Key))
        {
            Assert.Single(copies.Select(a => a.Payment.Id).Distinct());
            Assert.Single(copies, a => a.Status == HttpStatusCode.Created);   // the copy that won; the rest are replays
        }

        var keys = orders.Select(o => o.Key).ToArray();
        Assert.Equal(keys.Length, await PaymentsWithKeysAsync(keys));

        // 3. Nothing is lost on the way: the outbox drains, and the ledger has every captured payment, once.
        var captured = await ScalarAsync(platform.PaymentsDb, "SELECT count(*) FROM payments WHERE status = 'captured'");
        await Eventually.WaitUntilAsync(
            async () => await ScalarAsync(platform.PaymentsDb, "SELECT count(*) FROM outbox") == 0
                && await ScalarAsync(platform.LedgerDb, "SELECT count(*) FROM processed_payments") == captured,
            timeout: TimeSpan.FromSeconds(60));
        Assert.Equal(0, await ScalarAsync(platform.PaymentsDb, "SELECT count(*) FROM payments WHERE status = 'created'"));

        // 4. Money adds up across the services: what Payments.Api captured is what the ledger booked.
        var owedToMerchants = await BalancesAsync(platform.PaymentsDb, """
            SELECT 'merchant:' || merchant_id, sum(amount - fee)::bigint
            FROM payments WHERE status = 'captured' GROUP BY merchant_id
            """);
        var merchantAccounts = await BalancesAsync(platform.LedgerDb,
            "SELECT id, balance FROM accounts WHERE id LIKE 'merchant:%'");
        Assert.Equal(owedToMerchants, merchantAccounts);

        Assert.Equal(
            await ScalarAsync(platform.PaymentsDb, "SELECT coalesce(sum(fee), 0)::bigint FROM payments WHERE status = 'captured'"),
            await ScalarAsync(platform.LedgerDb, "SELECT coalesce(sum(balance), 0)::bigint FROM accounts WHERE id LIKE 'fee:%'"));
        Assert.Equal(
            -(await ScalarAsync(platform.PaymentsDb, "SELECT coalesce(sum(amount), 0)::bigint FROM payments WHERE status = 'captured'")),
            await ScalarAsync(platform.LedgerDb, "SELECT coalesce(sum(balance), 0)::bigint FROM accounts WHERE id LIKE 'clearing:%'"));

        // 5. And the ledger's own invariants: the check the load ladder prints after every step.
        var invariants = await platform.Ledger.GetFromJsonAsync<LedgerInvariants>("/invariants", Ct);
        Assert.NotNull(invariants);
        Assert.True(invariants.Ok, invariants.ToString());
        Assert.Equal(captured, invariants.Payments);

        output.WriteLine($"{answers.Count} requests, {keys.Length} payments, {captured} captured and booked by the ledger");
    }

    private async Task<(HttpStatusCode Status, PaymentResponse Payment)> PayAsync(Order order, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/payments")
        {
            Content = JsonContent.Create(new CreatePaymentRequest(order.MerchantId, order.Amount, "RUB")),
        };
        request.Headers.Add("Idempotency-Key", order.Key);

        using var response = await platform.Payments.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var problem = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException($"POST /payments answered {(int)response.StatusCode}: {problem}");
        }

        var payment = await response.Content.ReadFromJsonAsync<PaymentResponse>(cancellationToken);
        return (response.StatusCode, payment!);
    }

    private async Task<long> PaymentsWithKeysAsync(string[] keys)
    {
        await using var command = platform.PaymentsDb.CreateCommand("SELECT count(*) FROM payments WHERE idempotency_key = ANY($1)");
        command.Parameters.Add(new NpgsqlParameter<string[]> { TypedValue = keys });
        return (long)(await command.ExecuteScalarAsync(Ct))!;
    }

    private static async Task<long> ScalarAsync(NpgsqlDataSource database, string sql)
    {
        await using var command = database.CreateCommand(sql);
        return (long)(await command.ExecuteScalarAsync(Ct))!;
    }

    private static async Task<SortedDictionary<string, long>> BalancesAsync(NpgsqlDataSource database, string sql)
    {
        var balances = new SortedDictionary<string, long>(StringComparer.Ordinal);
        await using var command = database.CreateCommand(sql);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
            balances.Add(reader.GetString(0), reader.GetInt64(1));
        return balances;
    }

    private static Order NewOrder(Random random, int copies) =>
        new(Guid.NewGuid().ToString(), MerchantId: 1 + random.Next(Merchants), Amount: 1_000 + random.Next(99_001), copies);

    /// <summary>One payment a client wants made: a key, a body, and how many times the client sends it.</summary>
    private sealed record Order(string Key, long MerchantId, long Amount, int Copies);
}
