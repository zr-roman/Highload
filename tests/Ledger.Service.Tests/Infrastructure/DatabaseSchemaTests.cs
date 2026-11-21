using Ledger.Service.Ledger;
using Npgsql;
using Platform;
using static Ledger.Service.Tests.Infrastructure.LedgerData;

namespace Ledger.Service.Tests.Infrastructure;

/// <summary>
/// The ledger's schema.sql at the start of the service: instances that start together on an empty database, and a
/// restart on a database that already holds the books.
/// </summary>
[Collection("Postgres")]
public sealed class DatabaseSchemaTests(PostgresFixture db)
{
    private const int Instances = 4;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Instances_Starting_Together_On_An_Empty_Database_All_Apply_The_Schema()
    {
        await WithDatabaseAsync(async connectionString =>
        {
            // A pool per instance, like separate processes.
            var instances = Enumerable.Range(0, Instances).Select(_ => NpgsqlDataSource.Create(connectionString)).ToArray();
            try
            {
                foreach (var instance in instances)
                {
                    // Opened and returned to the pool: no instance loses time on a new connection when they start.
                    await using var connection = await instance.OpenConnectionAsync(Ct);
                }

                var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var starts = instances.Select(async instance =>
                {
                    await start.Task;
                    await DatabaseSchema.ApplyAsync(instance, PostgresFixture.SchemaPath, Ct);
                }).ToArray();
                start.SetResult();

                // Without the lock around the script, all of them but one fail with 23505 on pg_type.
                await Task.WhenAll(starts);

                await using var tables = instances[0].CreateCommand("""
                    SELECT count(*) FROM pg_tables
                    WHERE schemaname = 'public' AND tablename IN ('accounts', 'postings', 'processed_payments')
                    """);
                Assert.Equal(3L, await tables.ExecuteScalarAsync(Ct));
            }
            finally
            {
                foreach (var instance in instances)
                    await instance.DisposeAsync();
            }
        });
    }

    [Fact]
    public async Task Restart_Keeps_The_Books_And_The_Ledger_Goes_On_From_Them()
    {
        await db.ResetAsync(Ct);
        var writer = new LedgerWriter(db.DataSource);
        await writer.WriteBatchAsync(0, [Payment(42)], Ct);
        var accounts = await db.AccountsAsync(Ct);

        // Every start runs the whole script again: CREATE TABLE IF NOT EXISTS leaves the tables as they are.
        await DatabaseSchema.ApplyAsync(db.DataSource, PostgresFixture.SchemaPath, Ct);

        Assert.Equal(accounts, await db.AccountsAsync(Ct));
        Assert.Equal(3L, await db.CountPostingsAsync(Ct));

        Assert.Equal(1, await writer.WriteBatchAsync(0, [Payment(42)], Ct));
        Assert.Equal(6L, await db.CountPostingsAsync(Ct));
    }

    // A database of its own: the fixture's one has the schema already.
    private async Task WithDatabaseAsync(Func<string, Task> test)
    {
        var database = $"schema_{Guid.NewGuid():N}";
        await db.ExecuteAsync($"CREATE DATABASE {database}", Ct);
        try
        {
            await test(new NpgsqlConnectionStringBuilder(db.ConnectionString) { Database = database }.ConnectionString);
        }
        finally
        {
            await db.ExecuteAsync($"DROP DATABASE {database} WITH (FORCE)", Ct);
        }
    }
}
