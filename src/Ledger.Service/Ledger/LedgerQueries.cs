using Npgsql;

namespace Ledger.Service.Ledger;

public sealed record LedgerInvariants(long UnbalancedPayments, long MismatchedAccounts, long TotalBalance, long Payments)
{
    /// <summary>Money neither appeared nor disappeared.</summary>
    public bool Ok => UnbalancedPayments == 0 && MismatchedAccounts == 0 && TotalBalance == 0;
}

public sealed class LedgerQueries(NpgsqlDataSource dataSource)
{
    public async Task<long?> GetBalanceAsync(string accountId, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand("SELECT balance FROM accounts WHERE id = $1");
        command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = accountId });
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is long balance ? balance : null;
    }

    /// <summary>Platform income: the sum of the fee sub-accounts of all partitions.</summary>
    public async Task<long> GetPlatformFeesAsync(CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(
            "SELECT coalesce(sum(balance), 0)::bigint FROM accounts WHERE id LIKE 'fee:%'");
        return (long)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    /// <summary>
    /// The three checks from bench/ledger-check.sql. One statement sees one snapshot,
    /// so the checks agree with each other even while batches are being written.
    /// The statement reads every posting, so it gets slower as the ledger grows.
    /// </summary>
    public async Task<LedgerInvariants> CheckInvariantsAsync(CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        // The connection string's 10 s limit is meant for the consumer's batch writes; a full check of millions
        // of postings needs more. SET LOCAL ends with the transaction, so the pooled connection keeps its 10 s.
        await using (var timeout = new NpgsqlCommand("SET LOCAL statement_timeout = '2min'", connection, transaction))
            await timeout.ExecuteNonQueryAsync(cancellationToken);

        await using var command = new NpgsqlCommand("""
            SELECT
                (SELECT count(*)
                 FROM (SELECT payment_id FROM postings GROUP BY payment_id HAVING sum(amount) <> 0) AS t),
                (SELECT count(*)
                 FROM accounts AS a
                 LEFT JOIN (SELECT account_id, sum(amount) AS total FROM postings GROUP BY account_id) AS p
                        ON p.account_id = a.id
                 WHERE a.balance <> coalesce(p.total, 0)),
                (SELECT coalesce(sum(balance), 0)::bigint FROM accounts),
                (SELECT count(*) FROM processed_payments)
            """, connection, transaction);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);

        // Nothing to commit: the transaction only scopes SET LOCAL and is rolled back when disposed.
        return new LedgerInvariants(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3));
    }
}
