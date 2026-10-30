using System.Diagnostics;

namespace Payments.Api.Tests.Infrastructure;

/// <summary>
/// Waits for a condition in real time, checking it every few milliseconds. A fixed Task.Delay in a test
/// is either too long (slow tests) or too short (flaky ones); this returns as soon as the condition holds.
/// </summary>
public static class Eventually
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

    public static Task WaitUntilAsync(Func<bool> condition, TimeSpan? timeout = null) =>
        WaitUntilAsync(() => Task.FromResult(condition()), timeout);

    public static async Task WaitUntilAsync(Func<Task<bool>> condition, TimeSpan? timeout = null)
    {
        var limit = timeout ?? DefaultTimeout;
        var started = Stopwatch.GetTimestamp();

        while (!await condition())
        {
            if (Stopwatch.GetElapsedTime(started) > limit)
                throw new TimeoutException($"The condition was not met within {limit}");

            await Task.Delay(20, TestContext.Current.CancellationToken);
        }
    }
}
