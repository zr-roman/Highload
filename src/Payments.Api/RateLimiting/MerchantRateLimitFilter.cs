using Microsoft.Extensions.Options;
using Payments.Api.Payments;
using Payments.Api.Telemetry;
using StackExchange.Redis;
using System.Diagnostics;
using System.Globalization;

namespace Payments.Api.RateLimiting;

public sealed class MerchantRateLimitFilter(
    RedisTokenBucket redisTokenBucket, 
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

        Activity.Current?.SetTag("payments.merchant_id", request.MerchantId);

        if (breaker.ShouldBypass())
            return await BypassAsync(context, next);

        RateLimitDecision decision;
        try
        {
            decision = await redisTokenBucket
                .TryTakeAsync(
                    request.MerchantId.ToString(CultureInfo.InvariantCulture), 
                    settings.TokensPerSecond, 
                    settings.Burst)
                .WaitAsync(LimiterTimeout);
        }
        catch (Exception e) when (e is RedisConnectionException or TimeoutException)
        {
            // TimeoutException covers both WaitAsync and RedisTimeoutException (it derives from it).
            if (breaker.Trip())
                logger.LogWarning(e, "Rate limiter: Redis did not answer within {Timeout}; skipping the limit for {BreakDuration}",
                    LimiterTimeout, RedisCircuitBreaker.BreakDuration);

            return await BypassAsync(context, next);
        }

        if (breaker.Reset())
            logger.LogInformation("Rate limiter: Redis answers again; merchant limits are enforced");

        Activity.Current?.SetTag("payments.rate_limit", decision.Allowed ? "allowed" : "rejected");

        if (decision.Allowed)
            return await next(context);

        // Retry-After carries whole seconds only. Round up, and never send 0: it would mean "retry right away".
        var seconds = Math.Max(1, (int)Math.Ceiling(decision.RetryAfter.TotalSeconds));
        context.HttpContext.Response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);

        metrics.RateLimited();

        logger.LogDebug("Merchant {MerchantId} rate limited, retry after {RetryAfter}", request.MerchantId, decision.RetryAfter);

        return Results.Problem(
            statusCode: StatusCodes.Status429TooManyRequests, 
            title: "Too many requests", 
            detail: $"Merchant {request.MerchantId} exceeded its rate limit. Retry after {seconds} s.");
    }

    private async ValueTask<object?> BypassAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        Activity.Current?.SetTag("payments.rate_limit", "bypassed");
        metrics.RateLimitBypassed();
        return await next(context);
    }
}
