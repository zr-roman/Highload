using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Trace;
using StackExchange.Redis;
using StackExchange.Redis.KeyspaceIsolation;

namespace Platform;

public static class RedisExtensions
{
    /// <summary>
    /// One Redis connection per process and an IDatabase whose keys all start with the service's prefix.
    /// Redis holds only what can be lost (limits, notifications, caches), so the service starts
    /// and keeps working without it, and reconnects in the background.
    /// </summary>
    public static WebApplicationBuilder AddPlatformRedis(this WebApplicationBuilder builder, string keyPrefix)
    {
        var options = ConfigurationOptions.Parse(
            builder.Configuration.GetConnectionString("Redis")
            ?? throw new InvalidOperationException("Connection string 'Redis' is not configured."));

        options.AbortOnConnectFail = false;   // in code, not in config: it must not be forgotten

        builder.Services.AddSingleton<IConnectionMultiplexer>(services =>
        {
            var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("Platform.Redis");
            var redis = ConnectionMultiplexer.Connect(options);

            // Interactive only: the subscription connection reports the same outage a second time.
            redis.ConnectionFailed += (_, e) =>
            {
                if (e.ConnectionType == ConnectionType.Interactive)
                    logger.LogWarning(e.Exception, "Redis connection lost ({FailureType})", e.FailureType);
            };

            redis.ConnectionRestored += (_, e) =>
            {
                if (e.ConnectionType == ConnectionType.Interactive)
                    logger.LogInformation("Redis connection restored");
            };

            if (!redis.IsConnected)
                logger.LogWarning("Redis is not available at startup; working without it and reconnecting");

            return redis;
        });

        // Every key this service writes gets the prefix automatically, including keys passed to Lua scripts.
        builder.Services.AddSingleton(services =>
            services.GetRequiredService<IConnectionMultiplexer>().GetDatabase().WithKeyPrefix(keyPrefix));

        builder.Services.ConfigureOpenTelemetryTracerProvider(tracing => tracing.AddRedisInstrumentation());

        return builder;
    }
}