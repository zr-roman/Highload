using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Platform;

public static class TelemetryExtensions
{
    /// <summary>
    /// Traces, metrics and logs over OTLP. Without settings the exporter sends to http://localhost:4317,
    /// which is the dashboard from docker-compose.
    /// </summary>
    public static WebApplicationBuilder AddPlatformTelemetry(
        this WebApplicationBuilder builder, string serviceName, params string[] meters)
    {
        builder.Services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(serviceName))
            .WithTracing(tracing => tracing
                .AddAspNetCoreInstrumentation()
                .AddSource("System.Net.Http")   // HttpClient spans are built into .NET
                .AddNpgsql()                    // a span for every SQL command
                .AddSource(serviceName))        // the service's own spans
            .WithMetrics(metrics => metrics
                .AddAspNetCoreInstrumentation()
                .AddMeter("System.Runtime", "System.Net.Http", "Polly", "Npgsql")
                .AddMeter(meters))
            .WithLogging(configureBuilder: null, configureOptions: options =>
            {
                options.IncludeFormattedMessage = true;
                options.IncludeScopes = true;
            })
            .UseOtlpExporter();

        builder.Services.AddHealthChecks();
        return builder;
    }
}
