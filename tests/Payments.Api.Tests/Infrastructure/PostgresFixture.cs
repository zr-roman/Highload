using Npgsql;
using Platform;
using System.Data.Common;
using Testcontainers.PostgreSql;

namespace Payments.Api.Tests.Infrastructure;

public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer container = new PostgreSqlBuilder("postgres:17-alpine").Build();

    public NpgsqlDataSource DataSource { get; private set; } = null!;

    /// <summary>Every RecoveryHost opens its own pool with it, like a separate process.</summary>
    public string ConnectionString => container.GetConnectionString();

    public async ValueTask InitializeAsync()
    {
        await container.StartAsync();
        DataSource = NpgsqlDataSource.Create(container.GetConnectionString());
        await DatabaseSchema.ApplyAsync(DataSource, Path.Combine(AppContext.BaseDirectory, "Database", "schema.sql"));
    }

    public async ValueTask DisposeAsync()
    {
        await DataSource.DisposeAsync();
        await container.DisposeAsync();
    }

    // Every test starts from empty tables.
    public async Task ResetAsync(CancellationToken cancellationToken = default)
    {
        await using var command = DataSource.CreateCommand("TRUNCATE payments, outbox RESTART IDENTITY");
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<Guid> InsertPaymentAsync(TimeSpan age, string status = "created", CancellationToken cancellationToken = default)
    {
        var id = Guid.NewGuid();

        // Only the id, the status and the age vary; the idempotency key reuses the id, so it is unique as well.
        await using var command = DataSource.CreateCommand("""
        INSERT INTO payments (id, merchant_id, idempotency_key, request_hash, amount, currency, status, created_at)
        VALUES ($1, 42, $1::text, 'test', 150000, 'RUB', $2, now() - $3)
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

    /// <summary>Changes a payment behind recovery's back: this is how a test plays the endpoint that finished first.</summary>
    public async Task SetStatusAsync(Guid id, string status, CancellationToken cancellationToken = default)
    {
        await using var command = DataSource.CreateCommand("UPDATE payments SET status = $2, updated_at = now() WHERE id = $1");
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

    public async Task<long> CountOutboxAsync(CancellationToken cancellationToken = default)
    {
        await using var command = DataSource.CreateCommand("SELECT count(*) FROM outbox");
        return (long)(await command.ExecuteScalarAsync(cancellationToken))!;
    }
}

[CollectionDefinition("Postgres")]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>;
