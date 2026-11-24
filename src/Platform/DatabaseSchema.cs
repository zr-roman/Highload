using Npgsql;

namespace Platform;

public static class DatabaseSchema
{
    // Advisory lock keys share one space per database, so the services' own keys must differ from this one
    // (Payments.Api keeps them in AdvisoryLockKeys).
    private const long SchemaLockKey = 7_300_001;

    // The builds of CreateIndexConcurrentlyAsync, one at a time per database: a session-level lock, taken by tries.
    private const long IndexLockKey = 7_300_002;

    private static readonly TimeSpan IndexLockRetry = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Applies an idempotent schema script (CREATE ... IF NOT EXISTS) at startup.
    /// Deliberately simple; a real project would use migrations.
    /// </summary>
    /// <remarks>
    /// Instances that start together on an empty database take turns. Without the lock, two concurrent
    /// CREATE TABLE IF NOT EXISTS of one table both find no table, and the second fails with 23505
    /// (pg_type_typname_nsp_index) once the first commits.
    /// </remarks>
    public static Task ApplyAsync(
        NpgsqlDataSource dataSource, string scriptPath, CancellationToken cancellationToken = default) =>
        ApplyAsync(dataSource, scriptPath, afterCommitScriptPath: null, cancellationToken);

    /// <summary>
    /// The same, then the second script once the first has committed: for the steps that must not run while the
    /// first holds its locks, such as VALIDATE CONSTRAINT after an ADD CONSTRAINT ... NOT VALID.
    /// </summary>
    /// <remarks>
    /// A service whose second script may take long runs it with <see cref="ApplyAfterStartAsync"/> once it has
    /// started instead (Payments.Api, SchemaAfterStart): here it delays the start by the whole scan.
    /// </remarks>
    public static async Task ApplyAsync(
        NpgsqlDataSource dataSource, string scriptPath, string? afterCommitScriptPath,
        CancellationToken cancellationToken = default)
    {
        var sql = await File.ReadAllTextAsync(scriptPath, cancellationToken);

        await using (var connection = await dataSource.OpenConnectionAsync(cancellationToken))
        await using (var transaction = await connection.BeginTransactionAsync(cancellationToken))
        {
            // A transaction-level lock: the commit releases it, and so does the rollback if the script fails.
            await using var takeLock = new NpgsqlCommand("SELECT pg_advisory_xact_lock($1)", connection, transaction);
            
            takeLock.Parameters.Add(new NpgsqlParameter<long> { TypedValue = SchemaLockKey });
            await takeLock.ExecuteNonQueryAsync(cancellationToken);

            await using var command = new NpgsqlCommand(sql, connection, transaction);
            await command.ExecuteNonQueryAsync(cancellationToken);

            await transaction.CommitAsync(cancellationToken);
        }

        if (afterCommitScriptPath is not null)
            await ApplyAfterStartAsync(dataSource, afterCommitScriptPath, cancellationToken);
    }

    /// <summary>
    /// A script for after <see cref="ApplyAsync(NpgsqlDataSource, string, CancellationToken)"/> has committed, in a
    /// transaction of its own and without the advisory lock: instances that start together run it each, so it must
    /// be idempotent and safe to run concurrently, as VALIDATE CONSTRAINT is (the second waits for the first, then
    /// finds nothing to do).
    /// </summary>
    /// <remarks>
    /// A scan of a big table takes longer than the statement_timeout of the connection string and the 30 s
    /// CommandTimeout of Npgsql, so both are lifted for this transaction alone. SET LOCAL, not SET: the commit or the
    /// rollback drops it, a broken connection included, and no connection goes back to the pool without a timeout.
    /// A session-level SET would need a RESET afterwards, and a RESET on a broken connection fails and hides the
    /// error of the script.
    /// </remarks>
    public static async Task ApplyAfterStartAsync(
        NpgsqlDataSource dataSource, string scriptPath, CancellationToken cancellationToken = default)
    {
        var sql = await File.ReadAllTextAsync(scriptPath, cancellationToken);

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await using (var unlimited = new NpgsqlCommand("SET LOCAL statement_timeout = 0", connection, transaction))
            await unlimited.ExecuteNonQueryAsync(cancellationToken);

        await using (var command = new NpgsqlCommand(sql, connection, transaction) { CommandTimeout = 0 })
            await command.ExecuteNonQueryAsync(cancellationToken);

        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>
    /// An index on a table that takes writes all the time: CREATE INDEX CONCURRENTLY, which takes no lock that stops
    /// inserts or updates, so it runs after the start, like <see cref="ApplyAfterStartAsync"/>. A no-op if the index
    /// is there and valid. <paramref name="definition"/> is what follows ON, such as <c>payments (merchant_id)</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// CONCURRENTLY cannot run in a transaction, nor in a script of several statements (one implicit transaction),
    /// so it is a statement of its own on its own connection; with no transaction, statement_timeout is lifted by a
    /// session SET and reset afterwards, back to what the connection string gave.
    /// </para>
    /// <para>
    /// A build that fails or is killed midway, a pod stopped by Kubernetes for one, leaves the index behind INVALID:
    /// not used by queries, yet kept up by every write, and CREATE ... IF NOT EXISTS would see it and do nothing,
    /// for ever. So the state of the index is read rather than trusted to IF NOT EXISTS, and an invalid one is
    /// dropped and built again. To tell an invalid index from one that another instance is building right now, the
    /// builds take turns under an advisory lock of the session. It is taken by tries, pg_try_advisory_lock every
    /// <see cref="IndexLockRetry"/>, not by a waiting pg_advisory_lock: CONCURRENTLY waits for every transaction that
    /// is open when it starts, and a session waiting for the lock is in one, so each would wait for the other.
    /// </para>
    /// </remarks>
    public static async Task CreateIndexConcurrentlyAsync(
        NpgsqlDataSource dataSource, string name, string definition, CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);

        while (!await TryLockIndexBuildsAsync(connection, cancellationToken))
            await Task.Delay(IndexLockRetry, cancellationToken);

        try
        {
            await ExecuteAsync(connection, "SET statement_timeout = 0", cancellationToken);

            var valid = await IndexIsValidAsync(connection, name, cancellationToken);
            if (valid == true)
                return;

            if (valid == false)
                await ExecuteAsync(connection, $"DROP INDEX CONCURRENTLY IF EXISTS {name}", cancellationToken);

            await ExecuteAsync(connection, $"CREATE INDEX CONCURRENTLY {name} ON {definition}", cancellationToken);
        }
        finally
        {
            // A broken connection is not given back to the pool, and the server has dropped its lock with it.
            if (connection.State == System.Data.ConnectionState.Open)
            {
                await ExecuteAsync(connection, "RESET statement_timeout", CancellationToken.None);
                await using var unlock = new NpgsqlCommand("SELECT pg_advisory_unlock($1)", connection);
                unlock.Parameters.Add(new NpgsqlParameter<long> { TypedValue = IndexLockKey });
                await unlock.ExecuteNonQueryAsync(CancellationToken.None);
            }
        }
    }

    private static async Task<bool> TryLockIndexBuildsAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("SELECT pg_try_advisory_lock($1)", connection);
        command.Parameters.Add(new NpgsqlParameter<long> { TypedValue = IndexLockKey });
        return (bool)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    // true: there and valid; false: there and invalid; null: not there. In the schema of the search path, as the
    // CREATE INDEX that made it.
    private static async Task<bool?> IndexIsValidAsync(NpgsqlConnection connection, string name, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT i.indisvalid
            FROM pg_class c JOIN pg_index i ON i.indexrelid = c.oid
            WHERE c.relname = $1 AND c.relnamespace = current_schema()::regnamespace
            """, connection);
        command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = name });
        return await command.ExecuteScalarAsync(cancellationToken) is bool valid ? valid : null;
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection) { CommandTimeout = 0 };
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
