using Microsoft.Extensions.Diagnostics.HealthChecks;
using MongoDB.Bson;
using MongoDB.Driver;

namespace History.Service.History;

/// <summary>A ping of MongoDB, for readiness: without it the history can neither be written nor read.</summary>
public sealed class MongoHealthCheck(IMongoClient client) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        // An exception, a timeout included, is turned into the registration's failure status by the health check service.
        await client.GetDatabase("admin").RunCommandAsync<BsonDocument>(new BsonDocument("ping", 1), cancellationToken: cancellationToken);
        return HealthCheckResult.Healthy();
    }
}
