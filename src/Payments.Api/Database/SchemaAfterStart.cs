using Npgsql;
using Platform;

namespace Payments.Api.Database;

/// <summary>
/// Runs schema-after.sql once the service has started: VALIDATE CONSTRAINT after the NOT VALID of schema.sql.
/// </summary>
/// <remarks>
/// Not in the start itself: VALIDATE reads all of payments, and on a big table that is longer than the 60 s the
/// startup probe gives a pod (deploy/k8s/payments-api.yaml), so Kubernetes would kill the pod mid-scan, every time.
/// Here the pod takes payments while it runs: the CHECK is enforced on new rows from the ADD on, and VALIDATE only
/// marks the old ones checked. It holds one connection of the pool while it scans.
/// A transient error (the database out of reach, a failover) is tried again after a pause; any other one, such as
/// an old row the CHECK rejects (23514), is logged and not retried: the next start tries again, and until then the
/// constraint stays NOT VALID, which is safe for new rows.
/// </remarks>
public sealed class SchemaAfterStart(
    NpgsqlDataSource dataSource,
    IHostEnvironment environment,
    TimeProvider timeProvider,
    ILogger<SchemaAfterStart> logger) : BackgroundService
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(10);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var scriptPath = Path.Combine(environment.ContentRootPath, "Database", "schema-after.sql");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await DatabaseSchema.ApplyAfterStartAsync(dataSource, scriptPath, stoppingToken);
                logger.LogInformation("Schema after start: done");
                return;
            }
            catch (Exception e) when (!stoppingToken.IsCancellationRequested && StartupRetry.IsTransient(e))
            {
                logger.LogWarning("Schema after start failed, the next attempt in {Delay}: {Error}", RetryDelay, e.Message);
                await Task.Delay(RetryDelay, timeProvider, stoppingToken);
            }
            catch (Exception e) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(e, "Schema after start failed and will not be retried until the next start");
                return;
            }
        }
    }
}
