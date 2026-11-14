using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Routing;

namespace Platform;

public static class HealthCheckEndpoints
{
    /// <summary>
    /// The tag of the checks that decide whether this instance can serve its HTTP traffic right now.
    /// Only what the instance cannot work without: a dependency with a fallback or a buffer does not get it.
    /// </summary>
    public const string ReadyTag = "ready";

    /// <summary>
    /// /health/live for the liveness probe, /health/ready for the readiness probe, /health as an alias of ready.
    /// </summary>
    public static IEndpointRouteBuilder MapPlatformHealthChecks(this IEndpointRouteBuilder endpoints)
    {
        // Liveness: the process is up and answers HTTP. No dependency checks on purpose: with Postgres down,
        // a liveness check of Postgres would restart every instance at once, and none of them could start again
        // until Postgres came back.
        endpoints.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });

        // Readiness: the checks tagged ready. While one fails, the instance gets no requests but keeps running.
        var ready = new HealthCheckOptions { Predicate = check => check.Tags.Contains(ReadyTag) };
        endpoints.MapHealthChecks("/health/ready", ready);

        // The load matrix waits for /health before a run (load/Matrix/Stand.cs): the same as ready.
        endpoints.MapHealthChecks("/health", ready);

        return endpoints;
    }
}
