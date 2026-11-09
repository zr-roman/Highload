using System.Diagnostics.Metrics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Payments.Api.Payments;
using Payments.Api.RateLimiting;
using Payments.Api.Telemetry;
using Payments.Api.Tests.Infrastructure;

namespace Payments.Api.Tests.RateLimiting;

/// <summary>
/// The filter in front of POST /payments when Redis does not answer, and after. Stage 3 showed what happened before: the limit
/// went off for everyone, and the hot merchant got 512 payments a second at a limit of 200.
/// </summary>
public sealed class MerchantRateLimitFilterTests : IDisposable
{
    private static readonly object Passed = new();

    private readonly FakeTimeProvider time = new();
    private readonly FakeRedis redis = new();
    private readonly FakeLogger<MerchantRateLimitFilter> logs = new();
    private readonly ServiceProvider services = new ServiceCollection().AddMetrics().BuildServiceProvider();
    private readonly LocalMerchantLimiter localLimiter;
    private readonly MetricCollector<long> local;
    private readonly MerchantRateLimitFilter filter;

    public MerchantRateLimitFilterTests()
    {
        // A burst of 5: the sixth payment in a row is over the limit. A token every 10 s: the local bucket refills
        // in real time, and none comes back while a test runs.
        var options = Options.Create(new MerchantRateLimitOptions { TokensPerSecond = 0.1, Burst = 5 });
        var meterFactory = services.GetRequiredService<IMeterFactory>();
        local = new MetricCollector<long>(meterFactory, PaymentsMetrics.MeterName, "payments.rate_limit.local");

        localLimiter = new LocalMerchantLimiter(options);
        filter = new MerchantRateLimitFilter(
            redis, localLimiter, options, new PaymentsMetrics(meterFactory, time), new RedisCircuitBreaker(time),
            logs);
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
    // per Redis failure in the stage 3 matrix after step 4a.
    [Fact]
    public async Task An_Outage_Starts_From_What_The_Merchant_Has_Left()
    {
        for (var i = 0; i < 5; i++)
            Assert.Same(Passed, await PayAsync(merchantId: 1)); // Redis allows the whole burst

        redis.Hangs = true;
        var first = await PayAsync(merchantId: 1);

        Assert.Equal(StatusCodes.Status429TooManyRequests, Assert.IsAssignableFrom<IStatusCodeHttpResult>(first).StatusCode);
    }

    // During an outage the Redis bucket refills: it does not see the payments let through locally. Deciding alone after
    // the outage, it let a burst more through: about 400 payments over the limit per Redis failure in stage 3.
    [Fact]
    public async Task After_An_Outage_Redis_Does_Not_Let_A_Burst_More_Through()
    {
        redis.Hangs = true;
        for (var i = 0; i < 5; i++)
            Assert.Same(Passed, await PayAsync(merchantId: 1)); // the local bucket alone: the whole burst

        redis.Hangs = false; // and FakeRedis allows everything, as a bucket that refilled during the outage would
        time.Advance(RedisCircuitBreaker.BreakDuration);
        var probe = await PayAsync(merchantId: 1);
        var next = await PayAsync(merchantId: 1);

        Assert.Equal(StatusCodes.Status429TooManyRequests, Assert.IsAssignableFrom<IStatusCodeHttpResult>(probe).StatusCode);
        Assert.Equal(StatusCodes.Status429TooManyRequests, Assert.IsAssignableFrom<IStatusCodeHttpResult>(next).StatusCode);
        Assert.Equal(3, redis.Calls); // the call that hung, then two Redis allowed and the local bucket did not
    }

    // A payment Redis refuses does not pass, so it costs no local token. Otherwise a hot merchant sending far over
    // its limit would keep the local bucket empty, and it would refuse payments Redis allows.
    [Fact]
    public async Task A_Payment_Redis_Refuses_Costs_No_Local_Token()
    {
        redis.Refuses = true;
        for (var i = 0; i < 10; i++)
            await PayAsync(merchantId: 1);

        redis.Refuses = false;
        for (var i = 0; i < 5; i++)
            Assert.Same(Passed, await PayAsync(merchantId: 1));
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

    // How late Redis answered tells a slow Redis from a process too busy to take the answer.
    [Fact]
    public async Task The_Call_That_Timed_Out_Tells_When_Redis_Answered()
    {
        redis.Hangs = true;
        await PayAsync(merchantId: 1);

        redis.AnswerHungCalls();

        await Eventually.WaitUntilAsync(() => logs.Collector.GetSnapshot().Any(r => r.Message.Contains("answered after")));
        var warning = Assert.Single(logs.Collector.GetSnapshot(), r => r.Level == LogLevel.Warning);
        Assert.Contains("Redis did not answer", warning.Message); // load/Matrix counts these lines
        Assert.Null(warning.Exception); // WaitAsync's own TimeoutException tells nothing
    }

    private ValueTask<object?> PayAsync(long merchantId)
    {
        var context = new DefaultEndpointFilterInvocationContext(
            new DefaultHttpContext(), new CreatePaymentRequest(merchantId, Amount: 10_000, Currency: "RUB"));
        return filter.InvokeAsync(context, _ => ValueTask.FromResult<object?>(Passed));
    }

    /// <summary>
    /// Redis that answers "allowed" to everything, or "refused", or hangs: the filter's 100 ms timeout gives up on it,
    /// and the calls get their answer only when the test says so.
    /// </summary>
    private sealed class FakeRedis : IMerchantRateLimitStore
    {
        private static readonly RateLimitDecision Allowed = new(Allowed: true, RetryAfter: TimeSpan.Zero);

        private readonly TaskCompletionSource<RateLimitDecision> hung = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int calls;

        public bool Hangs { get; set; }

        public bool Refuses { get; set; }

        public int Calls => Volatile.Read(ref calls);

        public void AnswerHungCalls() => hung.SetResult(Allowed);

        public Task<RateLimitDecision> TryTakeAsync(string bucket, double tokensPerSecond, int burst)
        {
            Interlocked.Increment(ref calls);
            if (Hangs)
                return hung.Task;

            return Task.FromResult(Refuses ? new RateLimitDecision(Allowed: false, RetryAfter: TimeSpan.FromSeconds(1)) : Allowed);
        }
    }
}
