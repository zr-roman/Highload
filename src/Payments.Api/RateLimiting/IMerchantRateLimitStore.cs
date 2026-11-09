namespace Payments.Api.RateLimiting;

/// <summary>
/// The shared token buckets of the merchants: one per merchant for all instances of Payments.Api. In the service it is
/// Redis (<see cref="RedisTokenBucket"/>); the interface lets the tests put a Redis that does not answer in its place.
/// </summary>
public interface IMerchantRateLimitStore
{
    Task<RateLimitDecision> TryTakeAsync(string bucket, double tokensPerSecond, int burst);
}
