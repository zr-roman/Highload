using System.Diagnostics.Metrics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Payments.Api.Payments;
using Payments.Api.RateLimiting;
using Payments.Api.Telemetry;

namespace Payments.Api.Tests.RateLimiting;

/// <summary>
/// The filter in front of POST /payments when Redis does not answer. Stage 3 showed what happened before: the limit
/// went off for everyone, and the hot merchant got 512 payments a second at a limit of 200.
/// </summary>
public sealed class MerchantRateLimitFilterTests : IDisposable
{
    private static readonly object Passed = new();

    private readonly FakeTimeProvider time = new();
    private readonly FakeRedis redis = new();
    private readonly ServiceProvider services = new ServiceCollection().AddMetrics().BuildServiceProvider();
    private readonly LocalMerchantLimiter localLimiter;
    private readonly MetricCollector<long> local;
    private readonly MerchantRateLimitFilter filter;

    public MerchantRateLimitFilterTests()
    {
        // One token a second and a burst of 5: the sixth payment in a row is over the limit.
        var options = Options.Create(new MerchantRateLimitOptions { TokensPerSecond = 1, Burst = 5 });
        var meterFactory = services.GetRequiredService<IMeterFactory>();
        local = new MetricCollector<long>(meterFactory, PaymentsMetrics.MeterName, "payments.rate_limit.local");

        localLimiter = new LocalMerchantLimiter(options);
        filter = new MerchantRateLimitFilter(
            redis, localLimiter, options, new PaymentsMetrics(meterFactory, time), new RedisCircuitBreaker(time),
            NullLogger<MerchantRateLimitFilter>.Instance);
    }

    public void Dispose()
    {
        local.Dispose();
        localLimiter.Dispose();
        services.Dispose();
    }

    [Fact]
    public async Task Without_Redis_The_Merchant_Keeps_Its_Limit()
    {
        redis.Hangs = true;

        for (var i = 0; i < 5; i++)
            Assert.Same(Passed, await PayAsync(merchantId: 1));
        var sixth = await PayAsync(merchantId: 1);

        Assert.Equal(StatusCodes.Status429TooManyRequests, Assert.IsAssignableFrom<IStatusCodeHttpResult>(sixth).StatusCode);
        Assert.Equal(6, local.GetMeasurementSnapshot().Count);
    }

    // A local bucket that started full let a burst more through on every outage: 754-784 payments over the limit
    // per Redis failure in the stage 3 matrix after step 5.
    [Fact]
    public async Task An_Outage_Starts_From_What_The_Merchant_Has_Left()
    {
        for (var i = 0; i < 5; i++)
            Assert.Same(Passed, await PayAsync(merchantId: 1)); // Redis allows the whole burst

        redis.Hangs = true;
        var first = await PayAsync(merchantId: 1);

        Assert.Equal(StatusCodes.Status429TooManyRequests, Assert.IsAssignableFrom<IStatusCodeHttpResult>(first).StatusCode);
    }

    [Fact]
    public async Task After_One_Timeout_The_Others_Do_Not_Wait_For_Redis()
    {
        redis.Hangs = true;

        for (var i = 0; i < 5; i++)
            await PayAsync(merchantId: 1);

        Assert.Equal(1, redis.Calls);
    }

    [Fact]
    public async Task Without_Redis_One_Merchant_Does_Not_Use_Up_The_Limit_Of_Another()
    {
        redis.Hangs = true;
        for (var i = 0; i < 6; i++)
            await PayAsync(merchantId: 1);

        Assert.Same(Passed, await PayAsync(merchantId: 2));
    }

    [Fact]
    public async Task After_The_Break_One_Probe_Brings_Redis_Back()
    {
        redis.Hangs = true;
        await PayAsync(merchantId: 1);

        redis.Hangs = false;
        time.Advance(RedisCircuitBreaker.BreakDuration);
        await PayAsync(merchantId: 1); // the probe
        await PayAsync(merchantId: 1); // closed again: to Redis as well

        Assert.Equal(3, redis.Calls);
        Assert.Single(local.GetMeasurementSnapshot());
    }

    private ValueTask<object?> PayAsync(long merchantId)
    {
        var context = new DefaultEndpointFilterInvocationContext(
            new DefaultHttpContext(), new CreatePaymentRequest(merchantId, Amount: 10_000, Currency: "RUB"));
        return filter.InvokeAsync(context, _ => ValueTask.FromResult<object?>(Passed));
    }

    /// <summary>Redis that answers "allowed" to everything, or hangs: the filter's 100 ms timeout gives up on it.</summary>
    private sealed class FakeRedis : IMerchantRateLimitStore
    {
        private int calls;

        public bool Hangs { get; set; }

        public int Calls => Volatile.Read(ref calls);

        public Task<RateLimitDecision> TryTakeAsync(string bucket, double tokensPerSecond, int burst)
        {
            Interlocked.Increment(ref calls);
            return Hangs
                ? new TaskCompletionSource<RateLimitDecision>().Task
                : Task.FromResult(new RateLimitDecision(Allowed: true, RetryAfter: TimeSpan.Zero));
        }
    }
}
