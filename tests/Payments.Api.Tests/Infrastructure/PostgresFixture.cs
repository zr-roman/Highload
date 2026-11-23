using Contracts;
using Npgsql;
using Platform;
using System.Data.Common;
using Testcontainers.PostgreSql;

namespace Payments.Api.Tests.Infrastructure;

public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer container = new PostgreSqlBuilder("postgres:17-alpine").Build();

    public static string SchemaPath => Path.Combine(AppContext.BaseDirectory, "Database", "schema.sql");

    public static string SchemaAfterPath => Path.Combine(AppContext.BaseDirectory, "Database", "schema-after.sql");

    public NpgsqlDataSource DataSource { get; private set; } = null!;

    /// <summary>Every RecoveryHost opens its own pool with it, like a separate process.</summary>
    public string ConnectionString => container.GetConnectionString();

    public async ValueTask InitializeAsync()
    {
        await container.StartAsync();
        DataSource = NpgsqlDataSource.Create(container.GetConnectionString());
        await DatabaseSchema.ApplyAsync(DataSource, SchemaPath, SchemaAfterPath);
    }

    public async ValueTask DisposeAsync()
    {
        await DataSource.DisposeAsync();
        await container.DisposeAsync();
    }

    // Every test starts from empty tables.
    public async Task ResetAsync(CancellationToken cancellationToken = default)
    {
        await using var command = DataSource.CreateCommand("TRUNCATE payments, pending_payments, outbox RESTART IDENTITY");
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<Guid> InsertPaymentAsync(TimeSpan age, string status = "created", CancellationToken cancellationToken = default)
    {
        var id = Guid.NewGuid();

        // Only the id, the status and the age vary; the idempotency key reuses the id, so it is unique as well.
        // A payment in progress, 'created' or 'authorized', is also in pending_payments, as PaymentStore writes it.
        await using var command = DataSource.CreateCommand("""
        WITH payment AS (
            INSERT INTO payments (id, merchant_id, idempotency_key, request_hash, amount, currency, status, created_at)
            VALUES ($1, 42, $1::text, 'test', 150000, 'RUB', $2, now() - $3)
            RETURNING id, created_at, status
        )
        INSERT INTO pending_payments (id, created_at)
        SELECT id, created_at FROM payment WHERE status IN ('created', 'authorized')
        """);
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = id });
        command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = status });
        command.Parameters.Add(new NpgsqlParameter<TimeSpan> { TypedValue = age });

        await command.ExecuteNonQueryAsync(cancellationToken);
        return id;
    }

    internal async Task<string> GetStatusAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var command = DataSource.CreateCommand($"SELECT status FROM payments WHERE id = $1");
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = id });
        return (string)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    /// <summary>
    /// Changes a payment behind recovery's back: this is how a test plays the endpoint that finished first.
    /// Like the endpoint, it takes the payment out of pending_payments when the status is final: not 'created'
    /// and not 'authorized'.
    /// </summary>
    public async Task SetStatusAsync(Guid id, string status, CancellationToken cancellationToken = default)
    {
        await using var command = DataSource.CreateCommand("""
            WITH changed AS (UPDATE payments SET status = $2, updated_at = now() WHERE id = $1 RETURNING id, status)
            DELETE FROM pending_payments
            WHERE id IN (SELECT id FROM changed WHERE status IN ('captured', 'declined', 'voided'))
            """);
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = id });
        command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = status });
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<long?> GetFeeAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var command = DataSource.CreateCommand("SELECT fee FROM payments WHERE id = $1");
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = id });
        return await command.ExecuteScalarAsync(cancellationToken) is long fee ? fee : null;
    }

    public async Task<long> CountPendingAsync(CancellationToken cancellationToken = default)
    {
        await using var command = DataSource.CreateCommand("SELECT count(*) FROM pending_payments");
        return (long)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    public async Task<long> CountOutboxAsync(CancellationToken cancellationToken = default)
    {
        await using var command = DataSource.CreateCommand("SELECT count(*) FROM outbox");
        return (long)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    /// <summary>The outbox rows of one topic: a captured payment has one in each of two topics.</summary>
    public async Task<long> CountOutboxAsync(string topic, CancellationToken cancellationToken = default) =>
        (await OutboxAsync(topic, cancellationToken)).Count;

    /// <summary>The key and the payload of each outbox row of one topic, in the order they were written.</summary>
    public async Task<IReadOnlyList<(string Key, string Payload)>> OutboxAsync(string topic, CancellationToken cancellationToken = default)
    {
        await using var command = DataSource.CreateCommand("SELECT message_key, payload FROM outbox WHERE topic = $1 ORDER BY id");
        command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = topic });

        List<(string, string)> rows = [];
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            rows.Add((reader.GetString(0), reader.GetString(1)));

        return rows;
    }

    /// <summary>
    /// The PaymentFinalized the payment's row stands for: what its event in the outbox has to say, to the microsecond.
    /// </summary>
    public async Task<PaymentFinalized> FinalizedFromRowAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var command = DataSource.CreateCommand(
            "SELECT merchant_id, amount, fee, currency, status, created_at, updated_at FROM payments WHERE id = $1");
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = id });

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        Assert.True(await reader.ReadAsync(cancellationToken));

        return new PaymentFinalized(
            id, reader.GetInt64(0), reader.GetInt64(1), reader.IsDBNull(2) ? null : reader.GetInt64(2), reader.GetString(3),
            reader.GetString(4), reader.GetFieldValue<DateTimeOffset>(5), reader.GetFieldValue<DateTimeOffset>(6));
    }
}

[CollectionDefinition("Postgres")]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>;
