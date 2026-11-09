using Payments.Api.RateLimiting;
using Payments.Api.Tests.Infrastructure;

namespace Payments.Api.Tests.RateLimiting;

/// <summary>The limit of one instance for the time Redis does not answer: a token bucket per merchant, in real time.</summary>
public sealed class LocalMerchantLimiterTests : IDisposable
{
    // One token a second: nothing comes back while a test takes its tokens.
    private static readonly MerchantQuota Quota = new(TokensPerSecond: 1, Burst: 5, IsOverride: false);

    private readonly LocalMerchantLimiter limiter = new();

    public void Dispose() => limiter.Dispose();

    [Fact]
    public void The_Burst_Passes_And_The_Next_Payment_Waits()
    {
        for (var i = 0; i < 5; i++)
            Assert.True(limiter.TryTake("1", Quota).Allowed);

        var rejected = limiter.TryTake("1", Quota);

        Assert.False(rejected.Allowed);
        Assert.True(rejected.RetryAfter > TimeSpan.Zero);
    }

    [Fact]
    public void Each_Merchant_Has_A_Bucket_Of_Its_Own()
    {
        for (var i = 0; i < 6; i++)
            limiter.TryTake("1", Quota);

        Assert.True(limiter.TryTake("2", Quota).Allowed);
    }

    // A quota changed in appsettings.json acts at once, not when the old bucket refills.
    [Fact]
    public void A_New_Quota_Gets_A_Bucket_Of_Its_Own()
    {
        for (var i = 0; i < 6; i++)
            limiter.TryTake("1", Quota);

        Assert.True(limiter.TryTake("1", Quota with { Burst = 10, IsOverride = true }).Allowed);
    }

    [Fact]
    public async Task Tokens_Come_Back_With_Time()
    {
        var fast = new MerchantQuota(TokensPerSecond: 20, Burst: 1, IsOverride: false);
        Assert.True(limiter.TryTake("1", fast).Allowed);
        Assert.False(limiter.TryTake("1", fast).Allowed);

        // 20 a second: two tokens every 100 ms, from the timer of the partitioned limiter.
        await Eventually.WaitUntilAsync(() => limiter.TryTake("1", fast).Allowed, TimeSpan.FromSeconds(2));
    }
}
