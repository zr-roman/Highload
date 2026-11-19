using Npgsql;

namespace Platform;

public static class DatabaseSchema
{
    // Advisory lock keys share one space per database, so the services' own keys must differ from this one
    // (Payments.Api keeps them in AdvisoryLockKeys).
    private const long SchemaLockKey = 7_300_001;

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
            await using (var takeLock = new NpgsqlCommand("SELECT pg_advisory_xact_lock($1)", connection, transaction))
            {
                takeLock.Parameters.Add(new NpgsqlParameter<long> { TypedValue = SchemaLockKey });
                await takeLock.ExecuteNonQueryAsync(cancellationToken);
            }

            await using (var command = new NpgsqlCommand(sql, connection, transaction))
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
}
