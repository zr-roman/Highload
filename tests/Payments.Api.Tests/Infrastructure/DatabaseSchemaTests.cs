using Npgsql;
using Payments.Api.Database;
using Platform;

namespace Payments.Api.Tests.Infrastructure;

/// <summary>
/// Instances of a service that start together on an empty database apply its schema at the same moment;
/// a database of an older schema is migrated by the same start.
/// </summary>
[Collection("Postgres")]
public sealed class DatabaseSchemaTests(PostgresFixture db)
{
    private const int Instances = 4;

    // payments as schema.sql created it before stage 4: the CHECK of three statuses, under the name Postgres gives it.
    private const string PaymentsBeforeStage4 = """
        CREATE TABLE payments (
            id              uuid        PRIMARY KEY,
            merchant_id     bigint      NOT NULL,
            idempotency_key text        NOT NULL,
            request_hash    text        NOT NULL,
            amount          bigint      NOT NULL CHECK (amount > 0),
            currency        char(3)     NOT NULL,
            fee             bigint,
            status          text        NOT NULL CHECK (status IN ('created', 'captured', 'declined')),
            created_at      timestamptz NOT NULL DEFAULT now(),
            updated_at      timestamptz NOT NULL DEFAULT now(),
            CONSTRAINT payments_idempotency_key UNIQUE (merchant_id, idempotency_key)
        ) WITH (fillfactor = 80);
        """;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string SchemaPath => PostgresFixture.SchemaPath;

    private static string SchemaAfterPath => PostgresFixture.SchemaAfterPath;

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
                    await DatabaseSchema.ApplyAsync(instance, SchemaPath, SchemaAfterPath, Ct);
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
        });
    }

    [Fact]
    public async Task Old_Status_Check_Is_Replaced_Not_Valid_And_Validated_After_The_Commit()
    {
        await WithDatabaseAsync(async connectionString =>
        {
            await using var dataSource = NpgsqlDataSource.Create(connectionString);
            await ExecuteAsync(dataSource, PaymentsBeforeStage4);
            var created = await InsertAsync(dataSource, "created");
            var captured = await InsertAsync(dataSource, "captured");
            var declined = await InsertAsync(dataSource, "declined");

            // The main script alone, as if the start had stopped between the two: the new CHECK is there, not validated.
            await DatabaseSchema.ApplyAsync(dataSource, SchemaPath, Ct);
            var replaced = await ReadStatusCheckAsync(dataSource);
            Assert.Contains("'voided'", replaced.Definition);
            Assert.Contains("'authorized'", replaced.Definition);
            Assert.False(replaced.Validated);

            await DatabaseSchema.ApplyAsync(dataSource, SchemaPath, SchemaAfterPath, Ct);
            var validated = await ReadStatusCheckAsync(dataSource);
            Assert.True(validated.Validated);
            Assert.Equal(replaced.Oid, validated.Oid);      // validated in place, not replaced once more

            // The old rows are untouched, and the queue was filled with the one in progress.
            Assert.Equal("created", await StatusOfAsync(dataSource, created));
            Assert.Equal("captured", await StatusOfAsync(dataSource, captured));
            Assert.Equal("declined", await StatusOfAsync(dataSource, declined));
            await using var pending = dataSource.CreateCommand("SELECT id FROM pending_payments");
            Assert.Equal(created, await pending.ExecuteScalarAsync(Ct));

            await InsertAsync(dataSource, "authorized");
            await InsertAsync(dataSource, "voided");
        });
    }

    [Fact]
    public async Task Rerun_Of_The_Migration_Changes_Nothing()
    {
        await WithDatabaseAsync(async connectionString =>
        {
            await using var dataSource = NpgsqlDataSource.Create(connectionString);
            await ExecuteAsync(dataSource, PaymentsBeforeStage4);
            await DatabaseSchema.ApplyAsync(dataSource, SchemaPath, SchemaAfterPath, Ct);
            var migrated = await ReadStatusCheckAsync(dataSource);

            await DatabaseSchema.ApplyAsync(dataSource, SchemaPath, SchemaAfterPath, Ct);

            // A DROP and ADD again would give the constraint a new oid, and NOT VALID again would clear convalidated.
            Assert.Equal(migrated, await ReadStatusCheckAsync(dataSource));
            Assert.True(migrated.Validated);
        });
    }

    [Fact]
    public async Task New_Database_Gets_The_Status_Check_Valid_At_Once()
    {
        await WithDatabaseAsync(async connectionString =>
        {
            await using var dataSource = NpgsqlDataSource.Create(connectionString);
            await DatabaseSchema.ApplyAsync(dataSource, SchemaPath, Ct);

            var check = await ReadStatusCheckAsync(dataSource);
            Assert.Contains("'voided'", check.Definition);
            Assert.True(check.Validated);
        });
    }

    [Fact]
    public async Task Unknown_Status_Is_Rejected_Before_The_Validation_As_After_It()
    {
        await WithDatabaseAsync(async connectionString =>
        {
            await using var dataSource = NpgsqlDataSource.Create(connectionString);
            await ExecuteAsync(dataSource, PaymentsBeforeStage4);

            // NOT VALID skips the old rows only: a new row is checked from the ADD on.
            await DatabaseSchema.ApplyAsync(dataSource, SchemaPath, Ct);
            var notValid = await Assert.ThrowsAsync<PostgresException>(() => InsertAsync(dataSource, "refunded"));
            Assert.Equal(PostgresErrorCodes.CheckViolation, notValid.SqlState);

            await DatabaseSchema.ApplyAsync(dataSource, SchemaPath, SchemaAfterPath, Ct);
            var valid = await Assert.ThrowsAsync<PostgresException>(() => InsertAsync(dataSource, "refunded"));
            Assert.Equal(PostgresErrorCodes.CheckViolation, valid.SqlState);
            Assert.Equal("payments_status_check", valid.ConstraintName);
        });
    }

    [Fact]
    public async Task Validation_Restores_The_Statement_Timeout_Of_The_Connection_String()
    {
        await WithDatabaseAsync(async connectionString =>
        {
            // One connection in the pool: the one the validation ran on is the one read back.
            var builder = new NpgsqlConnectionStringBuilder(connectionString)
            {
                MaxPoolSize = 1,
                Options = "-c statement_timeout=5s",
            };
            await using var dataSource = NpgsqlDataSource.Create(builder.ConnectionString);
            await DatabaseSchema.ApplyAsync(dataSource, SchemaPath, SchemaAfterPath, Ct);

            await using var show = dataSource.CreateCommand("SHOW statement_timeout");
            Assert.Equal("5s", await show.ExecuteScalarAsync(Ct));
        });
    }

    [Fact]
    public async Task Check_With_Voided_But_Another_Set_Of_Statuses_Is_Replaced()
    {
        await WithDatabaseAsync(async connectionString =>
        {
            await using var dataSource = NpgsqlDataSource.Create(connectionString);
            await ExecuteAsync(dataSource, PaymentsBeforeStage4);
            // 'voided' is there, 'authorized' is not: a check for 'voided' alone would take it for migrated.
            await ExecuteAsync(dataSource, """
                ALTER TABLE payments DROP CONSTRAINT payments_status_check;
                ALTER TABLE payments ADD CONSTRAINT payments_status_check
                    CHECK (status IN ('created', 'captured', 'declined', 'voided'));
                """);

            await DatabaseSchema.ApplyAsync(dataSource, SchemaPath, SchemaAfterPath, Ct);

            Assert.Contains("'authorized'", (await ReadStatusCheckAsync(dataSource)).Definition);
            await InsertAsync(dataSource, "authorized");
        });
    }

    [Fact]
    public async Task Migration_Gives_Up_On_A_Held_Lock_Instead_Of_Queueing_The_Writes()
    {
        await WithDatabaseAsync(async connectionString =>
        {
            await using var dataSource = NpgsqlDataSource.Create(connectionString);
            await ExecuteAsync(dataSource, PaymentsBeforeStage4);

            // A long transaction that has read payments holds ACCESS SHARE until it ends; ALTER TABLE needs ACCESS
            // EXCLUSIVE. Without lock_timeout the migration would wait for it, and every write behind the migration.
            await using var holder = await dataSource.OpenConnectionAsync(Ct);
            await using var holding = await holder.BeginTransactionAsync(Ct);
            await using (var read = new NpgsqlCommand("SELECT count(*) FROM payments", holder, holding))
                await read.ExecuteScalarAsync(Ct);

            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            var failed = await Assert.ThrowsAsync<PostgresException>(
                () => DatabaseSchema.ApplyAsync(dataSource, SchemaPath, Ct));

            Assert.Equal(PostgresErrorCodes.LockNotAvailable, failed.SqlState);
            Assert.True(StartupRetry.IsTransient(failed));       // the start tries again rather than failing
            Assert.True(System.Diagnostics.Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(10));

            // Rolled back as a whole: the old CHECK is still there, and once the lock is gone the migration goes through.
            Assert.DoesNotContain("'voided'", (await ReadStatusCheckAsync(dataSource)).Definition);
            await holding.RollbackAsync(Ct);
            await DatabaseSchema.ApplyAsync(dataSource, SchemaPath, SchemaAfterPath, Ct);
            Assert.True((await ReadStatusCheckAsync(dataSource)).Validated);
        });
    }

    [Fact]
    public async Task Index_Built_Concurrently_Is_Valid_And_A_Rerun_Leaves_It_Alone()
    {
        await WithDatabaseAsync(async connectionString =>
        {
            await using var dataSource = NpgsqlDataSource.Create(connectionString);
            await DatabaseSchema.ApplyAsync(dataSource, SchemaPath, SchemaAfterPath, Ct);
            await InsertAsync(dataSource, "captured");

            var (name, definition) = Assert.Single(SchemaAfterStart.Indexes);
            await DatabaseSchema.CreateIndexConcurrentlyAsync(dataSource, name, definition, Ct);
            var first = await ReadIndexAsync(dataSource, name);
            await DatabaseSchema.CreateIndexConcurrentlyAsync(dataSource, name, definition, Ct);

            Assert.True(first.Valid);
            Assert.Equal(first, await ReadIndexAsync(dataSource, name));     // the same index: not dropped and built anew
            Assert.Contains("(merchant_id, created_at DESC, id DESC)", first.Definition);
        });
    }

    [Fact]
    public async Task Invalid_Index_Left_By_A_Failed_Build_Is_Built_Again()
    {
        await WithDatabaseAsync(async connectionString =>
        {
            await using var dataSource = NpgsqlDataSource.Create(connectionString);
            await DatabaseSchema.ApplyAsync(dataSource, SchemaPath, SchemaAfterPath, Ct);
            await InsertAsync(dataSource, "captured");
            await InsertAsync(dataSource, "captured");

            // A build that fails midway, as a pod killed during it would: two payments of merchant 42 break UNIQUE,
            // and the index stays behind, INVALID. IF NOT EXISTS would take it for done.
            var (name, definition) = Assert.Single(SchemaAfterStart.Indexes);
            await Assert.ThrowsAsync<PostgresException>(
                () => ExecuteAsync(dataSource, $"CREATE UNIQUE INDEX CONCURRENTLY {name} ON payments (merchant_id)"));
            Assert.False((await ReadIndexAsync(dataSource, name)).Valid);

            await DatabaseSchema.CreateIndexConcurrentlyAsync(dataSource, name, definition, Ct);

            var index = await ReadIndexAsync(dataSource, name);
            Assert.True(index.Valid);
            Assert.Contains("(merchant_id, created_at DESC, id DESC)", index.Definition);
        });
    }

    [Fact]
    public async Task Instances_Building_The_Index_Together_All_End_With_One_Valid_Index()
    {
        await WithDatabaseAsync(async connectionString =>
        {
            await using var dataSource = NpgsqlDataSource.Create(connectionString);
            await DatabaseSchema.ApplyAsync(dataSource, SchemaPath, SchemaAfterPath, Ct);
            await InsertAsync(dataSource, "captured");

            var (name, definition) = Assert.Single(SchemaAfterStart.Indexes);
            var instances = Enumerable.Range(0, Instances).Select(async _ =>
            {
                await using var instance = NpgsqlDataSource.Create(connectionString);
                await DatabaseSchema.CreateIndexConcurrentlyAsync(instance, name, definition, Ct);
            });
            await Task.WhenAll(instances);

            Assert.True((await ReadIndexAsync(dataSource, name)).Valid);
        });
    }

    [Fact]
    public async Task Index_Build_Restores_The_Statement_Timeout_Of_The_Connection_String()
    {
        await WithDatabaseAsync(async connectionString =>
        {
            var builder = new NpgsqlConnectionStringBuilder(connectionString)
            {
                MaxPoolSize = 1,
                Options = "-c statement_timeout=5s",
            };
            await using var dataSource = NpgsqlDataSource.Create(builder.ConnectionString);
            await DatabaseSchema.ApplyAsync(dataSource, SchemaPath, SchemaAfterPath, Ct);

            var (name, definition) = Assert.Single(SchemaAfterStart.Indexes);
            await DatabaseSchema.CreateIndexConcurrentlyAsync(dataSource, name, definition, Ct);

            await using var show = dataSource.CreateCommand("SHOW statement_timeout");
            Assert.Equal("5s", await show.ExecuteScalarAsync(Ct));
        });
    }

    private static async Task<IndexState> ReadIndexAsync(NpgsqlDataSource dataSource, string name)
    {
        await using var command = dataSource.CreateCommand("""
            SELECT c.oid, pg_get_indexdef(c.oid), i.indisvalid
            FROM pg_class c JOIN pg_index i ON i.indexrelid = c.oid
            WHERE c.relname = $1
            """);
        command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = name });
        await using var reader = await command.ExecuteReaderAsync(Ct);
        Assert.True(await reader.ReadAsync(Ct));
        return new IndexState(reader.GetFieldValue<uint>(0), reader.GetString(1), reader.GetBoolean(2));
    }

    // A database of its own: the fixture's one has the schema already.
    private async Task WithDatabaseAsync(Func<string, Task> test)
    {
        var database = $"schema_{Guid.NewGuid():N}";
        await ExecuteAsync(db.DataSource, $"CREATE DATABASE {database}");
        try
        {
            await test(new NpgsqlConnectionStringBuilder(db.ConnectionString) { Database = database }.ConnectionString);
        }
        finally
        {
            await ExecuteAsync(db.DataSource, $"DROP DATABASE {database} WITH (FORCE)");
        }
    }

    private static async Task<Guid> InsertAsync(NpgsqlDataSource dataSource, string status)
    {
        var id = Guid.NewGuid();
        await using var command = dataSource.CreateCommand("""
            INSERT INTO payments (id, merchant_id, idempotency_key, request_hash, amount, currency, status)
            VALUES ($1, 42, $1::text, 'test', 150000, 'RUB', $2)
            """);
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = id });
        command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = status });
        await command.ExecuteNonQueryAsync(Ct);
        return id;
    }

    private static async Task<string> StatusOfAsync(NpgsqlDataSource dataSource, Guid id)
    {
        await using var command = dataSource.CreateCommand("SELECT status FROM payments WHERE id = $1");
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = id });
        return (string)(await command.ExecuteScalarAsync(Ct))!;
    }

    private static async Task<StatusCheck> ReadStatusCheckAsync(NpgsqlDataSource dataSource)
    {
        await using var command = dataSource.CreateCommand("""
            SELECT oid, pg_get_constraintdef(oid), convalidated
            FROM pg_constraint
            WHERE conrelid = 'payments'::regclass AND conname = 'payments_status_check'
            """);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        Assert.True(await reader.ReadAsync(Ct));
        return new StatusCheck(reader.GetFieldValue<uint>(0), reader.GetString(1), reader.GetBoolean(2));
    }

    private static async Task ExecuteAsync(NpgsqlDataSource dataSource, string sql)
    {
        await using var command = dataSource.CreateCommand(sql);
        await command.ExecuteNonQueryAsync(Ct);
    }

    private sealed record StatusCheck(uint Oid, string Definition, bool Validated);

    private sealed record IndexState(uint Oid, string Definition, bool Valid);
}
