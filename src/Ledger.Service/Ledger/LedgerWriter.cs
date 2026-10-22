using Contracts;
using Npgsql;

namespace Ledger.Service.Ledger;

/// <summary>
/// Applies a batch of captured payments with a single SQL statement: one round trip and one implicit
/// transaction for the whole batch (experiment 5: about 25-33 times faster than an UPDATE per payment).
/// </summary>
public sealed class LedgerWriter(NpgsqlDataSource dataSource)
{
    // 1. batch        — the payments of the batch, without repeats inside the batch;
    // 2. fresh        — the ones not applied before (processed_payments filters out redelivered messages);
    // 3. new_postings — three postings per new payment: clearing (debit), merchant (credit), fee (credit);
    // 4. balances     — one upsert per touched account instead of one UPDATE per payment.
    // The statement returns how many payments were actually applied.
    private const string ApplyBatchSql = """
        WITH batch AS (
            SELECT DISTINCT ON (payment_id) payment_id, merchant_id, amount, fee
            FROM unnest($1::uuid[], $2::bigint[], $3::bigint[], $4::bigint[])
                 AS b (payment_id, merchant_id, amount, fee)
        ), fresh AS (
            INSERT INTO processed_payments (payment_id)
            SELECT payment_id FROM batch
            ON CONFLICT (payment_id) DO NOTHING
            RETURNING payment_id
        ), items AS (
            SELECT batch.* FROM batch JOIN fresh USING (payment_id)
        ), new_postings AS (
            INSERT INTO postings (payment_id, account_id, amount)
            SELECT payment_id, $5, -amount FROM items
            UNION ALL
            SELECT payment_id, 'merchant:' || merchant_id, amount - fee FROM items
            UNION ALL
            SELECT payment_id, $6, fee FROM items
            RETURNING account_id, amount
        ), balances AS (
            INSERT INTO accounts AS a (id, balance)
            SELECT account_id, sum(amount) FROM new_postings GROUP BY account_id
            ON CONFLICT (id) DO UPDATE SET balance = a.balance + EXCLUDED.balance
        )
        SELECT count(*) FROM fresh
        """;

    public async Task<int> WriteBatchAsync(
        int partition, IReadOnlyList<PaymentCaptured> payments, CancellationToken cancellationToken)
    {
        var ids = new Guid[payments.Count];
        var merchants = new long[payments.Count];
        var amounts = new long[payments.Count];
        var fees = new long[payments.Count];
        for (var i = 0; i < payments.Count; i++)
        {
            ids[i] = payments[i].PaymentId;
            merchants[i] = payments[i].MerchantId;
            amounts[i] = payments[i].Amount;
            fees[i] = payments[i].Fee;
        }

        await using var command = dataSource.CreateCommand(ApplyBatchSql);
        command.Parameters.Add(new NpgsqlParameter<Guid[]> { TypedValue = ids });
        command.Parameters.Add(new NpgsqlParameter<long[]> { TypedValue = merchants });
        command.Parameters.Add(new NpgsqlParameter<long[]> { TypedValue = amounts });
        command.Parameters.Add(new NpgsqlParameter<long[]> { TypedValue = fees });
        command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = LedgerAccounts.Clearing(partition) });
        command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = LedgerAccounts.Fee(partition) });

        var applied = (long)(await command.ExecuteScalarAsync(cancellationToken))!;
        return (int)applied;
    }
}
