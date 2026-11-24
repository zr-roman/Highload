using Npgsql;
using Platform;

namespace Payments.Api.Database;

/// <summary>
/// The steps of the schema that run once the service has started: schema-after.sql, VALIDATE CONSTRAINT after the
/// NOT VALID of schema.sql, then the indexes built CONCURRENTLY (<see cref="Indexes"/>).
/// </summary>
/// <remarks>
/// Not in the start itself: VALIDATE reads all of payments, and on a big table that is longer than the 60 s the
/// startup probe gives a pod (deploy/k8s/payments-api.yaml), so Kubernetes would kill the pod mid-scan, every time.
/// Here the pod takes payments while it runs: the CHECK is enforced on new rows from the ADD on, and VALIDATE only
/// marks the old ones checked. It holds one connection of the pool while it scans. An index built CONCURRENTLY scans
/// the table twice, with inserts and updates going on; until it is valid, the queries that want it read without it.
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

    /// <summary>
    /// payments_merchant_created: the list of a merchant's payments, newest first, page by page (PaymentList, stage 5).
    /// None of its columns ever changes after the insert, so the status change stays a HOT update (schema.sql); the
    /// price is one more index entry per insert.
    /// </summary>
    public static readonly (string Name, string Definition)[] Indexes =
    [
        ("payments_merchant_created", "payments (merchant_id, created_at DESC, id DESC)"),
    ];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var scriptPath = Path.Combine(environment.ContentRootPath, "Database", "schema-after.sql");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await DatabaseSchema.ApplyAfterStartAsync(dataSource, scriptPath, stoppingToken);
                foreach (var (name, definition) in Indexes)
                {
                    await DatabaseSchema.CreateIndexConcurrentlyAsync(dataSource, name, definition, stoppingToken);
                    logger.LogInformation("Schema after start: index {Index} is in place", name);
                }

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
