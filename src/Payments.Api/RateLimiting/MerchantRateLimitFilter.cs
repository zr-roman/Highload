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

        RateLimitDecision decision;
        try
        {
            decision = await store
                .TryTakeAsync(merchant, settings.TokensPerSecond, settings.Burst)
                .WaitAsync(LimiterTimeout);
        }
        catch (Exception e) when (e is RedisConnectionException or TimeoutException)
        {
            // TimeoutException covers both WaitAsync and RedisTimeoutException (it derives from it).
            // load/Matrix counts these lines: keep "Redis did not answer" in the text.
            if (breaker.Trip())
                logger.LogWarning(e, "Rate limiter: Redis did not answer within {Timeout}; using the local limit for {BreakDuration}",
                    LimiterTimeout, RedisCircuitBreaker.BreakDuration);

            return await LimitLocallyAsync(context, next, request.MerchantId, merchant);
        }

        if (breaker.TryClose(entered))
            logger.LogInformation("Rate limiter: Redis answers again; merchant limits are shared again");

        // The local bucket follows Redis, so that an outage starts from what the merchant has left (LocalMerchantLimiter).
        if (decision.Allowed)
            localLimiter.Spend(merchant);

        return await ApplyAsync(context, next, decision, request.MerchantId, source: "redis");
    }

    // Redis does not answer: the limit of this instance alone, not no limit at all (LocalMerchantLimiter).
    private ValueTask<object?> LimitLocallyAsync(
        EndpointFilterInvocationContext context, EndpointFilterDelegate next, long merchantId, string merchant)
    {
        metrics.RateLimitLocal();
        return ApplyAsync(context, next, localLimiter.TryTake(merchant), merchantId, source: "local");
    }

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
