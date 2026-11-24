using System.Text.Json;
using Contracts;
using Npgsql;

namespace Payments.Api.Payments;

/// <summary>
/// All SQL of the payments database. Every method is a single statement: one round trip and one implicit
/// transaction, so no transaction ever stays open between calls.
/// </summary>
public sealed class PaymentStore(NpgsqlDataSource dataSource)
{
    private const string SelectColumns = "id, merchant_id, idempotency_key, request_hash, amount, currency, fee, status, created_at";

    // The same columns of payments joined to pending_payments, which has an id and a created_at of its own.
    private static readonly string SelectPaymentColumns = string.Join(", ", SelectColumns.Split(", ").Select(column => $"p.{column}"));

    // What a final transition returns of the row it wrote: the columns of its event.
    private const string FinalColumns = "id, merchant_id, amount, fee, currency, status, created_at, updated_at";

    // The events of the final statuses (Contracts), built by the statement from the row it has just written, not by
    // the caller: amount, created_at and the time of the final status are then exactly what the database holds, and
    // the history of payments made from the events matches the list from payments to the microsecond. A caller's
    // clock would not: created_at is now() of the insert, and DateTimeOffset.UtcNow differs from it.
    // The names are those of ContractsJson (camelCase); PaymentStoreTests read the payloads back with it.
    private const string CapturedPayload = """
        json_build_object(
            'paymentId', id, 'merchantId', merchant_id, 'amount', amount, 'fee', fee, 'currency', currency,
            'capturedAt', updated_at, 'createdAt', created_at)::text
        """;

    private const string FinalizedPayload = """
        json_build_object(
            'paymentId', id, 'merchantId', merchant_id, 'amount', amount, 'currency', currency,
            'status', status, 'createdAt', created_at, 'finalizedAt', updated_at)::text
        """;

    /// <summary>
    /// Returns false when a payment with the same (merchant_id, idempotency_key) already exists:
    /// the unique constraint, not a SELECT before the INSERT, decides who was first.
    /// A new payment also gets its row in pending_payments, in the same statement: recovery finds it there.
    /// </summary>
    public async Task<bool> TryInsertAsync(Payment payment, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand("""
            WITH payment AS (
                INSERT INTO payments (id, merchant_id, idempotency_key, request_hash, amount, currency, status)
                VALUES ($1, $2, $3, $4, $5, $6, 'created')
                ON CONFLICT (merchant_id, idempotency_key) DO NOTHING
                RETURNING id, created_at
            )
            INSERT INTO pending_payments (id, created_at)
            SELECT id, created_at FROM payment
            """);

        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = payment.Id });
        command.Parameters.Add(new NpgsqlParameter<long> { TypedValue = payment.MerchantId });
        command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = payment.IdempotencyKey });
        command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = payment.RequestHash });
        command.Parameters.Add(new NpgsqlParameter<long> { TypedValue = payment.Amount });
        command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = payment.Currency });

        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    /// <summary>
    /// The bank approved: the money is held, the antifraud has not decided yet. Written on every approval, even when
    /// the decision comes right after it: every transition then has exactly one source status, and the row always shows
    /// where the saga stopped. 'authorized' is not final, so the payment stays in pending_payments: if the process
    /// dies before the decision, recovery finds the payment there and goes on from the antifraud, without asking the
    /// bank again.
    /// </summary>
    public async Task<bool> MarkAuthorizedAsync(Guid paymentId, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand("""
            UPDATE payments
            SET status = 'authorized', updated_at = now()
            WHERE id = $1 AND status = 'created'
            """);

        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = paymentId });

        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    /// <summary>
    /// The status change and the outbox row are written by one statement: they commit together,
    /// and the outbox row appears only if the payment really moved to 'captured'. One row, PaymentCaptured, for the
    /// ledger and the history alike. The same statement takes
    /// the payment out of pending_payments: captured is final. Of the statuses only 'authorized' is not final and
    /// keeps the payment in the queue, for recovery to finish the saga.
    /// Only from 'authorized': a payment is captured after the antifraud allowed it, or it failed open, never straight
    /// after the bank.
    /// The message key decides the partition, and so the Ledger consumer (Topics.PaymentsCapturedKey).
    /// </summary>
    /// <returns>The event as it went to the outbox; null if the payment had moved on and nothing was written.</returns>
    public async Task<PaymentCaptured?> MarkCapturedAsync(Guid paymentId, long fee, string messageKey, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand($"""
            WITH captured AS (
                UPDATE payments
                SET status = 'captured', fee = $2, updated_at = now()
                WHERE id = $1 AND status = 'authorized'
                RETURNING {FinalColumns}
            ),
            settled AS (
                DELETE FROM pending_payments WHERE id IN (SELECT id FROM captured)
            )
            INSERT INTO outbox (topic, message_key, payload)
            SELECT $3, $4, {CapturedPayload} FROM captured
            RETURNING payload
            """);

        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = paymentId });
        command.Parameters.Add(new NpgsqlParameter<long> { TypedValue = fee });
        command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = Topics.PaymentsCaptured });
        command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = messageKey });

        return await command.ExecuteScalarAsync(cancellationToken) is string payload
            ? JsonSerializer.Deserialize(payload, ContractsJson.Default.PaymentCaptured)
            : null;
    }

    /// <summary>
    /// The bank declined: a final status, so the payment leaves pending_payments in the same statement, as on capture,
    /// and its PaymentFinalized goes to the outbox with it. No PaymentCaptured: no money moved.
    /// </summary>
    public async Task<bool> MarkDeclinedAsync(Guid paymentId, string messageKey, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand($"""
            WITH declined AS (
                UPDATE payments
                SET status = 'declined', updated_at = now()
                WHERE id = $1 AND status = 'created'
                RETURNING {FinalColumns}
            ),
            settled AS (
                DELETE FROM pending_payments WHERE id IN (SELECT id FROM declined)
            )
            INSERT INTO outbox (topic, message_key, payload)
            SELECT $2, $3, {FinalizedPayload} FROM declined
            """);

        AddFinalizedParameters(command, paymentId, messageKey);

        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    /// <summary>
    /// The authorization is voided at the bank: the hold is gone, no money moves. A final status, so the payment leaves
    /// pending_payments in the same statement, as on capture and decline, and its PaymentFinalized goes to the outbox
    /// with it. No PaymentCaptured: the ledger hears only of captured payments, so a voided one never reached it, and
    /// there is nothing to undo there.
    /// From 'authorized', where the saga voids after a deny of the antifraud, and from 'created': the bank may already
    /// report the authorization of a payment still in 'created' as voided (AcquirerOutcome.Voided), and a voided
    /// authorization holds no money, whatever the row says.
    /// </summary>
    public async Task<bool> MarkVoidedAsync(Guid paymentId, string messageKey, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand($"""
            WITH voided AS (
                UPDATE payments
                SET status = 'voided', updated_at = now()
                WHERE id = $1 AND status IN ('created', 'authorized')
                RETURNING {FinalColumns}
            ),
            settled AS (
                DELETE FROM pending_payments WHERE id IN (SELECT id FROM voided)
            )
            INSERT INTO outbox (topic, message_key, payload)
            SELECT $2, $3, {FinalizedPayload} FROM voided
            """);

        AddFinalizedParameters(command, paymentId, messageKey);

        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    private static void AddFinalizedParameters(NpgsqlCommand command, Guid paymentId, string messageKey)
    {
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = paymentId });
        command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = Topics.PaymentsFinalized });
        command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = messageKey });
    }

    public async Task<Payment?> FindByIdAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand($"SELECT {SelectColumns} FROM payments WHERE id = $1");
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = id });
        return await ReadSingleAsync(command, cancellationToken);
    }

    public async Task<Payment?> FindByIdempotencyKeyAsync(long merchantId, string idempotencyKey, CancellationToken cancellationToken)
    {
        var sql = $"SELECT {SelectColumns} FROM payments WHERE merchant_id = $1 AND idempotency_key = $2";
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.Add(new NpgsqlParameter<long> { TypedValue = merchantId });
        command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = idempotencyKey });
        return await ReadSingleAsync(command, cancellationToken);
    }

    /// <summary>
    /// The oldest payments in progress, 'created' or 'authorized', from pending_payments, not all of them. The status
    /// is checked on payments all the same: a row of the queue whose payment is already final must not take a place
    /// in the batch.
    /// </summary>
    public async Task<IReadOnlyList<Payment>> FindStaleInProgressAsync(TimeSpan staleAfter, int batchSize, CancellationToken cancellationToken)
    {
        var sql = $"""
            SELECT {SelectPaymentColumns}
            FROM pending_payments q JOIN payments p ON p.id = q.id
            WHERE q.created_at < now() - $1 AND p.status IN ('created', 'authorized')
            ORDER BY q.created_at LIMIT $2
            """;
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.Add(new NpgsqlParameter<TimeSpan> { TypedValue = staleAfter });
        command.Parameters.Add(new NpgsqlParameter<int> { TypedValue = batchSize });

        List<Payment> res = [];

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
            res.Add(ConstructPayment(reader));

        return res;
    }

    /// <summary>
    /// When the oldest payment in progress of each status was written, null if there is none: for the metric of how
    /// long payments wait. One pass over pending_payments, which holds only the payments in progress.
    /// </summary>
    public async Task<(DateTimeOffset? Created, DateTimeOffset? Authorized)> FindOldestInProgressAsync(CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand("""
            SELECT min(q.created_at) FILTER (WHERE p.status = 'created'),
                   min(q.created_at) FILTER (WHERE p.status = 'authorized')
            FROM pending_payments q JOIN payments p ON p.id = q.id
            """);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);

        return (reader.IsDBNull(0) ? null : reader.GetFieldValue<DateTimeOffset>(0),
                reader.IsDBNull(1) ? null : reader.GetFieldValue<DateTimeOffset>(1));
    }

    private static async Task<Payment?> ReadSingleAsync(NpgsqlCommand command, CancellationToken cancellationToken)
    {
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        if (!await reader.ReadAsync(cancellationToken))
            return null;

        return ConstructPayment(reader);
    }

    private static Payment ConstructPayment(NpgsqlDataReader reader)
    {
        return new Payment(
            Id: reader.GetGuid(0),
            MerchantId: reader.GetInt64(1),
            IdempotencyKey: reader.GetString(2),
            RequestHash: reader.GetString(3),
            Amount: reader.GetInt64(4),
            Currency: reader.GetString(5),
            Fee: reader.IsDBNull(6) ? null : reader.GetInt64(6),
            Status: PaymentStatusDb.Parse(reader.GetString(7)),
            CreatedAt: reader.GetFieldValue<DateTimeOffset>(8));
    }
}
