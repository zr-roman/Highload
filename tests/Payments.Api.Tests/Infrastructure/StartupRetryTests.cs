using System.Net.Sockets;
using Confluent.Kafka;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Platform;

namespace Payments.Api.Tests.Infrastructure;

/// <summary>
/// A pod that starts while Postgres or Kafka is out of reach tries again; a mistake still fails the start at once.
/// </summary>
public sealed class StartupRetryTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_Dependency_Out_Of_Reach_Is_Tried_Again()
    {
        var attempts = 0;

        await StartupRetry.RunAsync("Test step", _ => ++attempts == 1
            ? Task.FromException(new NpgsqlException("Failed to connect", new SocketException((int)SocketError.TryAgain)))
            : Task.CompletedTask, NullLogger.Instance, Ct);

        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task A_Mistake_Fails_The_Start_At_Once()
    {
        var attempts = 0;

        await Assert.ThrowsAsync<InvalidOperationException>(() => StartupRetry.RunAsync("Test step", _ =>
        {
            attempts++;
            throw new InvalidOperationException("Broken schema script");
        }, NullLogger.Instance, Ct));

        Assert.Equal(1, attempts);
    }

    [Fact]
    public void Out_Of_Reach_Is_Told_From_A_Mistake()
    {
        Assert.True(StartupRetry.IsTransient(new NpgsqlException("Failed to connect", new SocketException((int)SocketError.TryAgain))));
        Assert.True(StartupRetry.IsTransient(new KafkaException(ErrorCode.Local_TimedOut)));
        Assert.False(StartupRetry.IsTransient(new PostgresException("password authentication failed", "FATAL", "FATAL", "28P01")));
        Assert.True(StartupRetry.IsTransient(new PostgresException("canceling statement due to lock timeout", "ERROR", "ERROR", "55P03")));
    }
}
