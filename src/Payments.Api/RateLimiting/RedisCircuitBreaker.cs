using System.Diagnostics;

namespace Payments.Api.RateLimiting;

/// <summary>
/// Skips Redis for a short time after it fails to answer, so a hung Redis costs
/// one timeout per break instead of one timeout per payment.
/// </summary>
public sealed class RedisCircuitBreaker
{
    public static readonly TimeSpan BreakDuration = TimeSpan.FromSeconds(5);

    private static readonly long BreakTicks = (long)(BreakDuration.TotalSeconds * Stopwatch.Frequency);

    // Stopwatch timestamp until which Redis is skipped; 0 = closed (Redis is used).
    private long _openUntil;

    /// <summary>True if the caller should skip Redis now.</summary>
    public bool ShouldBypass()
    {
        var until = Volatile.Read(ref _openUntil);
        if (until == 0)
            return false;                            // closed

        var now = Stopwatch.GetTimestamp();
        if (now < until)
            return true;                             // open

        // Half-open: the break is over. Exactly one caller wins this exchange and probes Redis;
        // it also starts a new break, so everyone else keeps bypassing until the probe answers.
        return Interlocked.CompareExchange(ref _openUntil, now + BreakTicks, until) != until;
    }

    /// <summary>Opens the breaker. Returns true only on the closed → open transition, so the caller logs once per outage.</summary>
    public bool Trip() =>
        Interlocked.Exchange(ref _openUntil, Stopwatch.GetTimestamp() + BreakTicks) == 0;

    /// <summary>Closes the breaker after Redis answered. Returns true only if it was open.</summary>
    public bool Reset() =>
        Volatile.Read(ref _openUntil) != 0 && Interlocked.Exchange(ref _openUntil, 0) != 0;
}
