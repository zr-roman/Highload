using System.Diagnostics.Metrics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Payments.Api.Payments;
using Payments.Api.RateLimiting;
using Payments.Api.Telemetry;
using Payments.Api.Tests.Infrastructure;

namespace Payments.Api.Tests.RateLimiting;

/// <summary>
/// The filter with the real token bucket in Redis: the quotas of stage 3, step 5. Merchant 1 has the default limit,
/// one token a second and a burst of 5; merchant 2 has a quota of its own, 100 a second and a burst of 20.
/// </summary>
[Collection("Redis")]
public sealed class MerchantRateLimitRedisTests : IDisposable
{
    private static readonly object Passed = new();

    private readonly TestOptionsMonitor<MerchantRateLimitOptions> options = new(Settings());
    private readonly ServiceProvider services = new ServiceCollection().AddMetrics().BuildServiceProvider();
    private readonly LocalMerchantLimiter localLimiter = new();
    private readonly MerchantQuotas quotas;
    private readonly MerchantRateLimitFilter filter;

    public MerchantRateLimitRedisTests(RedisFixture redis)
    {
        quotas = new MerchantQuotas(options, NullLogger<MerchantQuotas>.Instance);
        var time = new FakeTimeProvider();
        filter = new MerchantRateLimitFilter(
            new RedisTokenBucket(redis.NewDatabase()), localLimiter, quotas,
            new PaymentsMetrics(services.GetRequiredService<IMeterFactory>(), time), new RedisCircuitBreaker(time),
            NullLogger<MerchantRateLimitFilter>.Instance);
    }

    public void Dispose()
    {
        quotas.Dispose();
        localLimiter.Dispose();
        services.Dispose();
    }

    [Fact]
    public async Task A_Merchant_Over_The_Default_Limit_Gets_429_With_Retry_After()
    {
        for (var i = 0; i < 5; i++)
            Assert.Equal(StatusCodes.Status201Created, (await PayAsync(merchantId: 1)).Status);

        var (status, retryAfter) = await PayAsync(merchantId: 1);

        Assert.Equal(StatusCodes.Status429TooManyRequests, status);
        Assert.Equal("1", retryAfter); // a token a second: the next one comes within a second
    }

    [Fact]
    public async Task A_Merchant_With_A_Quota_Passes_Its_Own_Rate()
    {
        // The burst at once, then the rate: 100 a second gives 20 more tokens in 200 ms.
        for (var i = 0; i < 20; i++)
            Assert.Equal(StatusCodes.Status201Created, (await PayAsync(merchantId: 2)).Status);

        await Task.Delay(TimeSpan.FromMilliseconds(250), TestContext.Current.CancellationToken);

        for (var i = 0; i < 20; i++)
            Assert.Equal(StatusCodes.Status201Created, (await PayAsync(merchantId: 2)).Status);
    }

    [Fact]
    public async Task A_New_Quota_Applies_Without_A_Restart()
    {
        for (var i = 0; i < 6; i++)
            await PayAsync(merchantId: 1);
        Assert.Equal(StatusCodes.Status429TooManyRequests, (await PayAsync(merchantId: 1)).Status);

        // As appsettings.json edited by hand: merchant 1 gets a quota. Redis refills at the new rate from the next
        // request on, and the local bucket of the new quota starts full.
        options.Set(Settings(merchant1: new MerchantQuotaOptions { TokensPerSecond = 1000, Burst = 1000 }));

        await Eventually.WaitUntilAsync(async () => (await PayAsync(merchantId: 1)).Status == StatusCodes.Status201Created, TimeSpan.FromSeconds(2));
    }

    private async Task<(int Status, string? RetryAfter)> PayAsync(long merchantId)
    {
        var http = new DefaultHttpContext();
        var context = new DefaultEndpointFilterInvocationContext(http, new CreatePaymentRequest(merchantId, Amount: 10_000, Currency: "RUB"));
        var result = await filter.InvokeAsync(context, _ => ValueTask.FromResult<object?>(Passed));

        return result == Passed
            ? (StatusCodes.Status201Created, null)
            : (Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode ?? 0, http.Response.Headers.RetryAfter.ToString());
    }

    private static MerchantRateLimitOptions Settings(MerchantQuotaOptions? merchant1 = null)
    {
        var settings = new MerchantRateLimitOptions
        {
            TokensPerSecond = 1,
            Burst = 5,
            Overrides = { [2] = new MerchantQuotaOptions { TokensPerSecond = 100, Burst = 20 } },
        };
        if (merchant1 is not null)
            settings.Overrides[1] = merchant1;
        return settings;
    }
}
