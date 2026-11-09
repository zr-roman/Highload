using Microsoft.Extensions.Options;
using Payments.Api.Payments;
using Payments.Api.Telemetry;
using StackExchange.Redis;
using System.Diagnostics;
using System.Globalization;

namespace Payments.Api.RateLimiting;

public sealed class MerchantRateLimitFilter(
    IMerchantRateLimitStore store,
    LocalMerchantLimiter localLimiter,
    IOptions<MerchantRateLimitOptions> options,
    PaymentsMetrics metrics,
    RedisCircuitBreaker breaker,
    ILogger<MerchantRateLimitFilter> logger) : IEndpointFilter
{
    private static readonly TimeSpan LimiterTimeout = TimeSpan.FromMilliseconds(100);

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var settings = options.Value;

        if (!settings.Enabled)
            return await next(context);

        var request = context.Arguments.OfType<CreatePaymentRequest>().First();
        var merchant = request.MerchantId.ToString(CultureInfo.InvariantCulture);

        Activity.Current?.SetTag("payments.merchant_id", request.MerchantId);

        var entered = breaker.Enter();
        if (entered == RedisBreakerState.Open)
            return await LimitLocallyAsync(context, next, request.MerchantId, merchant);

        // If Redis does not answer in time, the warning tells what this process was doing meanwhile.
        var started = Stopwatch.GetTimestamp();
        var gcPauseBefore = GC.GetTotalPauseDuration();

        var call = store.TryTakeAsync(merchant, settings.TokensPerSecond, settings.Burst);
        RateLimitDecision decision;
        try
        {
            decision = await call.WaitAsync(LimiterTimeout);
        }
        catch (Exception e) when (e is RedisConnectionException or TimeoutException)
        {
            // TimeoutException covers both WaitAsync and RedisTimeoutException (it derives from it).
            // load/Matrix counts these lines: keep "Redis did not answer" in the text.
            if (breaker.Trip())
            {
                // A failed call says why in its exception. One WaitAsync gave up on has none worth logging,
                // and LogWhenFinished tells what became of it.
                logger.LogWarning(call.IsFaulted ? e : null,
                    "Rate limiter: Redis did not answer within {Timeout}; using the local limit for {BreakDuration}. " +
                    "Meanwhile GC paused the process for {GcPause} ms; thread pool: {Threads} threads, {Queued} work items waiting",
                    LimiterTimeout, RedisCircuitBreaker.BreakDuration, (int)(GC.GetTotalPauseDuration() - gcPauseBefore).TotalMilliseconds,
                    ThreadPool.ThreadCount, ThreadPool.PendingWorkItemCount);

                if (!call.IsCompleted)
                    LogWhenFinished(call, started, gcPauseBefore);
            }

            return await LimitLocallyAsync(context, next, request.MerchantId, merchant);
        }

        if (breaker.TryClose(entered))
            logger.LogInformation("Rate limiter: Redis answers again; merchant limits are shared again");

        // What Redis allows, the local bucket must allow too: it keeps the two buckets in step (LocalMerchantLimiter).
        if (decision.Allowed && localLimiter.TryTake(merchant) is { Allowed: false } local)
            return await ApplyAsync(context, next, local, request.MerchantId, source: "local");

        return await ApplyAsync(context, next, decision, request.MerchantId, source: "redis");
    }

    // Redis does not answer: the limit of this instance alone, not no limit at all (LocalMerchantLimiter).
    private ValueTask<object?> LimitLocallyAsync(
        EndpointFilterInvocationContext context, EndpointFilterDelegate next, long merchantId, string merchant)
    {
        metrics.RateLimitLocal();
        return ApplyAsync(context, next, localLimiter.TryTake(merchant), merchantId, source: "local");
    }

    // Once per outage, for the call that opened the breaker: did Redis answer late, and how late, or fail? If GC
    // paused the process for most of that time, or many work items were waiting, the delay was in this process;
    // if neither, in Redis or the way to it.
    private void LogWhenFinished(Task<RateLimitDecision> call, long started, TimeSpan gcPauseBefore) =>
        call.ContinueWith(finished => logger.LogInformation(finished.Exception?.InnerException,
                "Rate limiter: the Redis call that timed out {Outcome} after {Elapsed} ms; GC paused the process for {GcPause} ms of them",
                finished.IsCompletedSuccessfully ? "answered" : "failed",
                (int)Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                (int)(GC.GetTotalPauseDuration() - gcPauseBefore).TotalMilliseconds),
            TaskScheduler.Default);

    private async ValueTask<object?> ApplyAsync(
        EndpointFilterInvocationContext context, EndpointFilterDelegate next, RateLimitDecision decision, long merchantId, string source)
    {
        Activity.Current?.SetTag("payments.rate_limit", decision.Allowed ? "allowed" : "rejected");
        Activity.Current?.SetTag("payments.rate_limit.source", source);

        if (decision.Allowed)
            return await next(context);

        // Retry-After carries whole seconds only. Round up, and never send 0: it would mean "retry right away".
        var seconds = Math.Max(1, (int)Math.Ceiling(decision.RetryAfter.TotalSeconds));
        context.HttpContext.Response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);

        metrics.RateLimited(source);

        logger.LogDebug("Merchant {MerchantId} rate limited by {Source}, retry after {RetryAfter}", merchantId, source, decision.RetryAfter);

        return Results.Problem(
            statusCode: StatusCodes.Status429TooManyRequests,
            title: "Too many requests",
            detail: $"Merchant {merchantId} exceeded its rate limit. Retry after {seconds} s.");
    }
}
