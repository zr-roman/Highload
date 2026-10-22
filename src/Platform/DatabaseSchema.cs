using Npgsql;

namespace Platform;

public static class DatabaseSchema
{
    /// <summary>
    /// Applies an idempotent schema script (CREATE ... IF NOT EXISTS) at startup.
    /// Deliberately simple; a real project would use migrations.
    /// </summary>
    public static async Task ApplyAsync(
        NpgsqlDataSource dataSource, string scriptPath, CancellationToken cancellationToken = default)
    {
        var sql = await File.ReadAllTextAsync(scriptPath, cancellationToken);
        await using var command = dataSource.CreateCommand(sql);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
