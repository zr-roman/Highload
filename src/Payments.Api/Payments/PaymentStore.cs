using System.Globalization;
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
    private const string SelectColumns =
        "id, merchant_id, idempotency_key, request_hash, amount, currency, fee, status, created_at";

    /// <summary>
    /// Returns false when a payment with the same (merchant_id, idempotency_key) already exists:
    /// the unique constraint, not a SELECT before the INSERT, decides who was first.
    /// </summary>
    public async Task<bool> TryInsertAsync(Payment payment, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand("""
            INSERT INTO payments (id, merchant_id, idempotency_key, request_hash, amount, currency, status)
            VALUES ($1, $2, $3, $4, $5, $6, 'created')
            ON CONFLICT (merchant_id, idempotency_key) DO NOTHING
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
    /// The status change and the outbox row are written by one statement: they commit together,
    /// and the outbox row appears only if the payment really moved out of 'created'.
    /// </summary>
    public async Task<bool> MarkCapturedAsync(PaymentCaptured captured, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand("""
            WITH captured AS (
                UPDATE payments
                SET status = 'captured', fee = $2, updated_at = now()
                WHERE id = $1 AND status = 'created'
                RETURNING id
            )
            INSERT INTO outbox (topic, message_key, payload)
            SELECT $3, $4, $5 FROM captured
            """);
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = captured.PaymentId });
        command.Parameters.Add(new NpgsqlParameter<long> { TypedValue = captured.Fee });
        command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = Topics.PaymentsCaptured });
        // The key decides the partition: all payments of a merchant go to the same Ledger consumer.
        command.Parameters.Add(new NpgsqlParameter<string>
        {
            TypedValue = captured.MerchantId.ToString(CultureInfo.InvariantCulture),
        });
        command.Parameters.Add(new NpgsqlParameter<string>
        {
            TypedValue = JsonSerializer.Serialize(captured, ContractsJson.Default.PaymentCaptured),
        });
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async Task<bool> MarkDeclinedAsync(Guid paymentId, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand("""
            UPDATE payments
            SET status = 'declined', updated_at = now()
            WHERE id = $1 AND status = 'created'
            """);
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = paymentId });
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async Task<Payment?> FindByIdAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand($"SELECT {SelectColumns} FROM payments WHERE id = $1");
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = id });
        return await ReadSingleAsync(command, cancellationToken);
    }

    public async Task<Payment?> FindByIdempotencyKeyAsync(
        long merchantId, string idempotencyKey, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(
            $"SELECT {SelectColumns} FROM payments WHERE merchant_id = $1 AND idempotency_key = $2");
        command.Parameters.Add(new NpgsqlParameter<long> { TypedValue = merchantId });
        command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = idempotencyKey });
        return await ReadSingleAsync(command, cancellationToken);
    }

    private static async Task<Payment?> ReadSingleAsync(NpgsqlCommand command, CancellationToken cancellationToken)
    {
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return null;

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
