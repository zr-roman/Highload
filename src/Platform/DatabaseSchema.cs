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
    /// The second script runs with no transaction of its own and without the advisory lock: instances that start
    /// together run it each, so it must be idempotent and safe to run concurrently.
    /// </remarks>
    public static async Task ApplyAsync(
        NpgsqlDataSource dataSource, string scriptPath, string? afterCommitScriptPath,
        CancellationToken cancellationToken = default)
    {
        var sql = await File.ReadAllTextAsync(scriptPath, cancellationToken);
        var afterCommitSql = afterCommitScriptPath is null
            ? null
            : await File.ReadAllTextAsync(afterCommitScriptPath, cancellationToken);

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);

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

        if (afterCommitSql is not null)
            await ApplyAfterCommitAsync(connection, afterCommitSql, cancellationToken);
    }

    private static async Task ApplyAfterCommitAsync(
        NpgsqlConnection connection, string sql, CancellationToken cancellationToken)
    {
        // A scan of a big table takes longer than the statement_timeout of the connection string and the 30 s
        // CommandTimeout of Npgsql. With no transaction SET LOCAL has nothing to apply to, so the session setting is
        // lifted and put back: the connection goes back to the pool for the service's own statements.
        await using (var unlimited = new NpgsqlCommand("SET statement_timeout = 0", connection))
            await unlimited.ExecuteNonQueryAsync(cancellationToken);

        try
        {
            await using var command = new NpgsqlCommand(sql, connection) { CommandTimeout = 0 };
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            await using var reset = new NpgsqlCommand("RESET statement_timeout", connection);
            await reset.ExecuteNonQueryAsync(CancellationToken.None);
        }
    }
}
