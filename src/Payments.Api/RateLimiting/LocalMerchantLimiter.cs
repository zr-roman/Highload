using System.Threading.RateLimiting;

namespace Payments.Api.RateLimiting;

/// <summary>
/// The merchant limit kept in the memory of this process. It decides alone while Redis does not answer: without it
/// a hot merchant gets everything it sends until Redis is back (stage 3, run 5c: 512 payments a second at a limit
/// of 200). While Redis answers, it keeps in step with Redis.
/// </summary>
/// <remarks>
/// While Redis answers, a payment passes only if both Redis and the local bucket allow it, and both lose a token
/// for it. So the two stay in step: when an outage starts, the local bucket holds about what the merchant has left,
/// not a full burst; and after it, Redis, whose bucket refilled meanwhile because it did not see the payments let
/// through locally, cannot let a burst more through on its own. Stage 3 of the load tests measured both: about
/// 750 payments over the limit per Redis failure with a local bucket that started full, about 400 with Redis
/// alone deciding after the failure.
/// It is still an approximation:
/// <list type="bullet">
/// <item>Each instance of Payments.Api has its own buckets with the full limit and sees only its own payments,
/// so with N instances a merchant gets up to N times the limit while Redis is down. Nor does the bucket keep in step
/// then: an instance that sees a part of the merchant's payments has a full bucket when the outage starts. In stage 3
/// a 10 s outage cost about 3 000 payments over the limit with two instances, under 100 with one. Bounded, unlike
/// no limit; a share of the limit per instance would need the number of instances.</item>
/// <item>A payment Redis allowed and the local bucket refused has cost a Redis token all the same. Right after an
/// outage that is the point: Redis's extra burst is spent on payments that do not pass. At the limit it costs a
/// few payments: the local bucket gets its tokens every tenth of a second, Redis's refills all the time. Each
/// such refusal leaves a token in the local bucket, so they stop by themselves.</item>
/// </list>
/// </remarks>
public sealed class LocalMerchantLimiter : IDisposable
{
    // A token bucket per merchant and quota, created on its first request. The quota is part of the key: when it
    // changes (MerchantQuotas), the merchant gets a new bucket at once, and the old one goes when it has stood full
    // for 10 s, as every idle partition does. AutoReplenishment = false: instead of a timer per merchant, the
    // partitioned limiter refills all its buckets from one timer of its own, every 100 ms, by the rate times the
    // time since the last refill: 20 tokens every 100 ms at 200 a second.
    private readonly PartitionedRateLimiter<(string Merchant, MerchantQuota Quota)> _limiter =
        PartitionedRateLimiter.Create<(string Merchant, MerchantQuota Quota), (string, MerchantQuota)>(key =>
            RateLimitPartition.GetTokenBucketLimiter(key, _ => Bucket(key.Quota)));

    /// <summary>Takes a token of the merchant if there is one: the decision of the local limit.</summary>
    public RateLimitDecision TryTake(string merchant, MerchantQuota quota)
    {
        using var lease = _limiter.AttemptAcquire((merchant, quota));
        if (lease.IsAcquired)
            return new RateLimitDecision(Allowed: true, RetryAfter: TimeSpan.Zero);

        // The token bucket tells when its next tokens come; a second if it does not.
        return new RateLimitDecision(
            Allowed: false,
            RetryAfter: lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter) ? retryAfter : TimeSpan.FromSeconds(1));
    }

    public void Dispose() => _limiter.Dispose();

    private static TokenBucketRateLimiterOptions Bucket(MerchantQuota quota)
    {
        // Rate = TokensPerPeriod / ReplenishmentPeriod, and TokensPerPeriod is a whole number: a tenth of a second's
        // worth, with the period set so that the rate stays exact, also for rates below 10 a second.
        var tokensPerPeriod = Math.Max(1, (int)Math.Round(quota.TokensPerSecond / 10));

        return new TokenBucketRateLimiterOptions
        {
            TokenLimit = quota.Burst,
            TokensPerPeriod = tokensPerPeriod,
            ReplenishmentPeriod = TimeSpan.FromSeconds(tokensPerPeriod / quota.TokensPerSecond),
            QueueLimit = 0, // decide at once: a payment does not wait for a token
            AutoReplenishment = false,
        };
    }
}
