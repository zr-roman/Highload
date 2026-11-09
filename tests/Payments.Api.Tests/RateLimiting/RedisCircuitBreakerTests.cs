using System.Collections.Concurrent;
using Microsoft.Extensions.Time.Testing;
using Payments.Api.RateLimiting;

namespace Payments.Api.Tests.RateLimiting;

/// <summary>The breaker on a fake clock: which requests go to Redis, and which answer may close it again.</summary>
public sealed class RedisCircuitBreakerTests
{
    private static readonly TimeSpan JustBefore = RedisCircuitBreaker.BreakDuration - TimeSpan.FromMilliseconds(1);

    private readonly FakeTimeProvider time = new();
    private readonly RedisCircuitBreaker breaker;

    public RedisCircuitBreakerTests() => breaker = new RedisCircuitBreaker(time);

    [Fact]
    public void A_New_Breaker_Sends_Requests_To_Redis() =>
        Assert.Equal(RedisBreakerState.Closed, breaker.Enter());

    [Fact]
    public void A_Failure_Keeps_Requests_Away_From_Redis_For_The_Break()
    {
        breaker.Trip();

        time.Advance(JustBefore);

        Assert.Equal(RedisBreakerState.Open, breaker.Enter());
    }

    [Fact]
    public void After_The_Break_Exactly_One_Request_Probes_Redis()
    {
        breaker.Trip();
        time.Advance(RedisCircuitBreaker.BreakDuration);

        var states = new ConcurrentBag<RedisBreakerState>();
        Parallel.For(0, 100, _ => states.Add(breaker.Enter()));

        Assert.Single(states, state => state == RedisBreakerState.Probe);
        Assert.Equal(99, states.Count(state => state == RedisBreakerState.Open));
    }

    [Fact]
    public void An_Answer_To_The_Probe_Closes_The_Breaker()
    {
        breaker.Trip();
        time.Advance(RedisCircuitBreaker.BreakDuration);
        var probe = breaker.Enter();

        Assert.True(breaker.TryClose(probe));
        Assert.Equal(RedisBreakerState.Closed, breaker.Enter());
    }

    [Fact]
    public void A_Failed_Probe_Starts_Another_Break()
    {
        breaker.Trip();
        time.Advance(RedisCircuitBreaker.BreakDuration);
        Assert.Equal(RedisBreakerState.Probe, breaker.Enter());

        Assert.False(breaker.Trip()); // the outage goes on: nothing new to log

        time.Advance(JustBefore);
        Assert.Equal(RedisBreakerState.Open, breaker.Enter());
        time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Equal(RedisBreakerState.Probe, breaker.Enter());
    }

    // The flapping of stage 3: a request sent to Redis before it hung got its answer after another request had
    // timed out, and closed the breaker at once, so the next requests waited for the hung Redis again.
    [Fact]
    public void A_Late_Answer_From_Before_The_Failure_Does_Not_Close_The_Breaker()
    {
        var early = breaker.Enter();
        Assert.Equal(RedisBreakerState.Closed, early);

        breaker.Trip();

        Assert.False(breaker.TryClose(early));
        Assert.Equal(RedisBreakerState.Open, breaker.Enter());
    }

    [Fact]
    public void Only_The_First_Failure_Of_An_Outage_Opens_The_Breaker_Anew()
    {
        Assert.True(breaker.Trip());
        Assert.False(breaker.Trip());

        time.Advance(RedisCircuitBreaker.BreakDuration);
        breaker.TryClose(breaker.Enter());

        Assert.True(breaker.Trip()); // a new outage
    }
}
