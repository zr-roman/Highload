using Npgsql;
using System.Diagnostics;

namespace Payments.Api.Database;

/// <summary>
/// Сессионная блокировка на отдельном соединении;
/// процесс умер — соединение закрылось — лидером стал другой экземпляр;
/// соединение простаивает, но не в состоянии «idle in transaction».
/// Пока объект жив, сервис — лидер, а DisposeAsync отпускает блокировку.
/// </summary>
internal sealed class AdvisoryLeaderLock : IAsyncDisposable
{
    private readonly NpgsqlConnection connection;
    private AdvisoryLeaderLock(NpgsqlConnection connection) => this.connection = connection;
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(5);

    internal static async Task<AdvisoryLeaderLock> AcquireAsync(NpgsqlDataSource dataSource, long lockKey, string jobName, ILogger logger, CancellationToken cancellationToken)
    {
        var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        try
        {
            logger.LogInformation("{JobName} is waiting for leadership", jobName);

            var started = Stopwatch.GetTimestamp();

            while (!await TryBecomeLeaderAsync(lockKey, connection, cancellationToken))
                await Task.Delay(RetryInterval, cancellationToken);

            logger.LogInformation("{JobName} is the leader on {MachineName}, process {ProcessId}. Became the leader after {WaitTime}", jobName, Environment.MachineName, Environment.ProcessId, Stopwatch.GetElapsedTime(started));

            return new AdvisoryLeaderLock(connection);
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    private static async Task<bool> TryBecomeLeaderAsync(long leaderLockKey, NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("SELECT pg_try_advisory_lock($1)", connection);
        command.Parameters.Add(new NpgsqlParameter<long> { TypedValue = leaderLockKey });
        return (bool)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    public ValueTask DisposeAsync() => connection.DisposeAsync();
}
