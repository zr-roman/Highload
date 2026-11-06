using Npgsql;
using Platform;

namespace Payments.Api.Tests.Infrastructure;

/// <summary>
/// Instances of a service that start together on an empty database apply its schema at the same moment.
/// </summary>
[Collection("Postgres")]
public sealed class DatabaseSchemaTests(PostgresFixture db)
{
    private const int Instances = 4;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string SchemaPath => Path.Combine(AppContext.BaseDirectory, "Database", "schema.sql");

    [Fact]
    public async Task Instances_Starting_Together_On_An_Empty_Database_All_Apply_The_Schema()
    {
        // A database of its own: the fixture's one has the schema already.
        var database = $"schema_{Guid.NewGuid():N}";
        await ExecuteAsync(db.DataSource, $"CREATE DATABASE {database}");
        try
        {
            var connectionString = new NpgsqlConnectionStringBuilder(db.ConnectionString) { Database = database }.ConnectionString;

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
                    await DatabaseSchema.ApplyAsync(instance, SchemaPath, Ct);
                }).ToArray();
                start.SetResult();

                // Without a lock around the script, all of them but one fail with 23505 on pg_type.
                await Task.WhenAll(starts);

                await using var tables = instances[0].CreateCommand(
                    "SELECT count(*) FROM pg_tables WHERE schemaname = 'public' AND tablename IN ('payments', 'outbox')");
                Assert.Equal(2L, await tables.ExecuteScalarAsync(Ct));
            }
            finally
            {
                foreach (var instance in instances)
                    await instance.DisposeAsync();
            }
        }
        finally
        {
            await ExecuteAsync(db.DataSource, $"DROP DATABASE {database} WITH (FORCE)");
        }
    }

    private static async Task ExecuteAsync(NpgsqlDataSource dataSource, string sql)
    {
        await using var command = dataSource.CreateCommand(sql);
        await command.ExecuteNonQueryAsync(Ct);
    }
}
