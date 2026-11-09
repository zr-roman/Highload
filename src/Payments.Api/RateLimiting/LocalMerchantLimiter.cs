using System.Threading.RateLimiting;
using Microsoft.Extensions.Options;

namespace Payments.Api.RateLimiting;

/// <summary>
/// The merchant limit kept in the memory of this process, for the time Redis does not answer: without it a hot
/// merchant gets everything it sends until Redis is back (stage 3, run 5c: 512 payments a second at a limit of 200).
/// </summary>
/// <remarks>
/// It is an approximation, and it errs on the side of the merchant:
/// <list type="bullet">
/// <item>Each instance of Payments.Api has its own buckets with the full limit, so with N instances a merchant gets up
/// to N times the limit while Redis is down. Bounded, unlike no limit; a share of the limit per instance would need
/// the number of instances.</item>
/// <item>The local bucket is not kept in step with Redis: it is full when an outage starts, so every outage may let
/// up to <see cref="MerchantRateLimitOptions.Burst"/> payments of a merchant through at once.</item>
/// </list>
/// </remarks>
public sealed class LocalMerchantLimiter : IDisposable
{
    private readonly PartitionedRateLimiter<string> _limiter;

    public LocalMerchantLimiter(IOptions<MerchantRateLimitOptions> options)
    {
        var settings = options.Value;

        // TokensPerPeriod is a whole number. Tokens come in portions of about a tenth of a second, 20 every 100 ms
        // at 200 a second, not 200 at once every second; the period is set so that the rate stays exact.
        var tokensPerPeriod = Math.Max(1, (int)Math.Round(settings.TokensPerSecond / 10));
        var period = TimeSpan.FromSeconds(tokensPerPeriod / settings.TokensPerSecond);

        // A token bucket per merchant, created on its first request. AutoReplenishment = false: the partitioned
        // limiter refills all its buckets from one timer of its own, instead of a timer per merchant.
        _limiter = PartitionedRateLimiter.Create<string, string>(merchant =>
            RateLimitPartition.GetTokenBucketLimiter(merchant, _ => new TokenBucketRateLimiterOptions
            {
                TokenLimit = settings.Burst,
                TokensPerPeriod = tokensPerPeriod,
                ReplenishmentPeriod = period,
                QueueLimit = 0, // decide at once: a payment does not wait for a token
                AutoReplenishment = false,
            }));
    }

    public RateLimitDecision TryTake(string merchant)
    {
        using var lease = _limiter.AttemptAcquire(merchant);
        if (lease.IsAcquired)
            return new RateLimitDecision(Allowed: true, RetryAfter: TimeSpan.Zero);

        // The token bucket tells when its next tokens come; a second if it does not.
        return new RateLimitDecision(
            Allowed: false,
            RetryAfter: lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter) ? retryAfter : TimeSpan.FromSeconds(1));
    }

    public void Dispose() => _limiter.Dispose();
}
