using Npgsql;
using Platform;
using Testcontainers.PostgreSql;

namespace Ledger.Service.Tests.Infrastructure;

/// <summary>
/// The ledger's database in a container, with the schema applied by the same call as at the start of the service.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    // The same image as in the tests of Payments.Api.
    private readonly PostgreSqlContainer container = new PostgreSqlBuilder("postgres:17-alpine").Build();

    // Ledger.Service.csproj copies schema.sql to its output, and so to the output of the tests.
    public static string SchemaPath => Path.Combine(AppContext.BaseDirectory, "Database", "schema.sql");

    public NpgsqlDataSource DataSource { get; private set; } = null!;

    /// <summary>For a database of a test's own.</summary>
    public string ConnectionString => container.GetConnectionString();

    public async ValueTask InitializeAsync()
    {
        await container.StartAsync();
        DataSource = NpgsqlDataSource.Create(container.GetConnectionString());
        await DatabaseSchema.ApplyAsync(DataSource, SchemaPath);
    }

    public async ValueTask DisposeAsync()
    {
        await DataSource.DisposeAsync();
        await container.DisposeAsync();
    }

    // Every test starts from an empty ledger.
    public async Task ResetAsync(CancellationToken cancellationToken = default) =>
        await ExecuteAsync("TRUNCATE accounts, postings, processed_payments RESTART IDENTITY", cancellationToken);

    /// <summary>Every balance by account id, in the form <see cref="LedgerData.Balances"/> builds the expected ones.</summary>
    public async Task<SortedDictionary<string, long>> AccountsAsync(CancellationToken cancellationToken = default)
    {
        await using var command = DataSource.CreateCommand("SELECT id, balance FROM accounts");
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        var accounts = new SortedDictionary<string, long>(StringComparer.Ordinal);
        while (await reader.ReadAsync(cancellationToken))
            accounts.Add(reader.GetString(0), reader.GetInt64(1));

        return accounts;
    }

    /// <summary>The postings of a payment, by account id. A payment booked twice has each of them twice.</summary>
    public async Task<List<(string Account, long Amount)>> PostingsOfAsync(
        Guid paymentId, CancellationToken cancellationToken = default)
    {
        await using var command = DataSource.CreateCommand("SELECT account_id, amount FROM postings WHERE payment_id = $1");
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = paymentId });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        var postings = new List<(string Account, long Amount)>();
        while (await reader.ReadAsync(cancellationToken))
            postings.Add((reader.GetString(0), reader.GetInt64(1)));

        // Ordered here and not by ORDER BY: the order of text in Postgres depends on the collation of the database.
        return postings.OrderBy(p => p.Account, StringComparer.Ordinal).ThenBy(p => p.Amount).ToList();
    }

    public async Task<long> CountPostingsAsync(CancellationToken cancellationToken = default)
    {
        await using var command = DataSource.CreateCommand("SELECT count(*) FROM postings");
        return (long)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    public async Task<long> CountProcessedPaymentsAsync(CancellationToken cancellationToken = default)
    {
        await using var command = DataSource.CreateCommand("SELECT count(*) FROM processed_payments");
        return (long)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    public async Task ExecuteAsync(string sql, CancellationToken cancellationToken = default)
    {
        await using var command = DataSource.CreateCommand(sql);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}

[CollectionDefinition("Postgres")]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>;
