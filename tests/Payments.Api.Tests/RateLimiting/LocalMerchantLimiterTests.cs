using Microsoft.Extensions.Options;
using Payments.Api.RateLimiting;
using Payments.Api.Tests.Infrastructure;

namespace Payments.Api.Tests.RateLimiting;

/// <summary>The limit of one instance for the time Redis does not answer: a token bucket per merchant, in real time.</summary>
public sealed class LocalMerchantLimiterTests : IDisposable
{
    // One token a second: nothing comes back while a test takes its tokens.
    private readonly LocalMerchantLimiter limiter = Create(tokensPerSecond: 1, burst: 5);

    public void Dispose() => limiter.Dispose();

    [Fact]
    public void The_Burst_Passes_And_The_Next_Payment_Waits()
    {
        for (var i = 0; i < 5; i++)
            Assert.True(limiter.TryTake("1").Allowed);

        var rejected = limiter.TryTake("1");

        Assert.False(rejected.Allowed);
        Assert.True(rejected.RetryAfter > TimeSpan.Zero);
    }

    [Fact]
    public void Tokens_Spent_While_Redis_Answers_Are_Gone_When_It_Stops()
    {
        for (var i = 0; i < 5; i++)
            limiter.Spend("1");

        Assert.False(limiter.TryTake("1").Allowed);
    }

    [Fact]
    public void Each_Merchant_Has_A_Bucket_Of_Its_Own()
    {
        for (var i = 0; i < 6; i++)
            limiter.TryTake("1");

        Assert.True(limiter.TryTake("2").Allowed);
    }

    [Fact]
    public async Task Tokens_Come_Back_With_Time()
    {
        using var fast = Create(tokensPerSecond: 20, burst: 1);
        Assert.True(fast.TryTake("1").Allowed);
        Assert.False(fast.TryTake("1").Allowed);

        // 20 a second: two tokens every 100 ms, from the timer of the partitioned limiter.
        await Eventually.WaitUntilAsync(() => fast.TryTake("1").Allowed, TimeSpan.FromSeconds(2));
    }

    private static LocalMerchantLimiter Create(double tokensPerSecond, int burst) =>
        new(Options.Create(new MerchantRateLimitOptions { TokensPerSecond = tokensPerSecond, Burst = burst }));
}
