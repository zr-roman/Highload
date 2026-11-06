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
    public static async Task ApplyAsync(
        NpgsqlDataSource dataSource, string scriptPath, CancellationToken cancellationToken = default)
    {
        var sql = await File.ReadAllTextAsync(scriptPath, cancellationToken);

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

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
}
