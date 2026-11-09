namespace Payments.Api.RateLimiting;

/// <summary>What a request does with Redis, as the breaker tells it on the way in.</summary>
public enum RedisBreakerState
{
    /// <summary>Redis answers: ask it.</summary>
    Closed,

    /// <summary>Redis failed a moment ago: do not ask it, use the local limit.</summary>
    Open,

    /// <summary>The break is over and this request is the one that tries Redis again.</summary>
    Probe,
}

/// <summary>
/// Skips Redis for a short time after it fails to answer, so a hung Redis costs one timeout per break instead
/// of one timeout per payment. Only the probe closes it: a request that went to Redis before the failure
/// and got its answer late says nothing about Redis now.
/// </summary>
public sealed class RedisCircuitBreaker(TimeProvider time)
{
    public static readonly TimeSpan BreakDuration = TimeSpan.FromSeconds(5);

    private readonly long _breakTicks = (long)(BreakDuration.TotalSeconds * time.TimestampFrequency);

    // Timestamp of the TimeProvider until which Redis is skipped; 0 = closed (Redis is used).
    private long _openUntil;

    public RedisBreakerState Enter()
    {
        var until = Volatile.Read(ref _openUntil);
        if (until == 0)
            return RedisBreakerState.Closed;

        var now = time.GetTimestamp();
        if (now < until)
            return RedisBreakerState.Open;

        // The break is over. Exactly one caller wins this exchange and probes Redis; it also starts a new break,
        // so everyone else keeps away from Redis until the probe has an answer.
        return Interlocked.CompareExchange(ref _openUntil, now + _breakTicks, until) == until
            ? RedisBreakerState.Probe
            : RedisBreakerState.Open;
    }

    /// <summary>
    /// Redis did not answer: opens the breaker for <see cref="BreakDuration"/> from now. Returns true only on the
    /// closed → open transition, so that the caller logs once per outage, not once per late request or failed probe.
    /// </summary>
    public bool Trip() =>
        Interlocked.Exchange(ref _openUntil, time.GetTimestamp() + _breakTicks) == 0;

    /// <summary>
    /// Redis answered a request that entered as <paramref name="entered"/>. Closes the breaker only for the probe;
    /// returns true if it did. The answer to a request that entered while closed changes nothing: if the breaker
    /// has opened since, that answer came from before the failure.
    /// </summary>
    public bool TryClose(RedisBreakerState entered) =>
        entered == RedisBreakerState.Probe && Interlocked.Exchange(ref _openUntil, 0) != 0;
}
