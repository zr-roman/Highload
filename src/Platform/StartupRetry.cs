using System.Diagnostics;
using System.Net.Sockets;
using Confluent.Kafka;
using Confluent.Kafka.Admin;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Platform;

/// <summary>
/// A startup step that needs Postgres or Kafka over the network, tried again for a while instead of failing the start
/// on the first error. A pod may start while a dependency is briefly out of reach: in the first minute after a node
/// goes down, its CoreDNS replica is still in the Service, and every other DNS query hangs until it times out.
/// Failing the start there costs more than the outage itself: Kubernetes restarts the container after 10 s,
/// then 20 s, then 40 s, and the replica the HPA has just added stays out of the balancing all that time.
/// </summary>
public static class StartupRetry
{
    // Attempts stop 20 s into a step: both steps and the rest of the start fit in the 60 s that the startup probe
    // gives a pod (deploy/k8s/payments-api.yaml).
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan FirstDelay = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan MaxDelay = TimeSpan.FromSeconds(4);

    public static async Task RunAsync(
        string step, Func<CancellationToken, Task> action, ILogger logger, CancellationToken cancellationToken = default)
    {
        var started = Stopwatch.GetTimestamp();
        var delay = FirstDelay;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await action(cancellationToken);
                if (attempt > 1)
                    logger.LogInformation("{Step}: done on attempt {Attempt}", step, attempt);
                return;
            }
            catch (Exception e) when (IsTransient(e) && Stopwatch.GetElapsedTime(started) + delay < Budget)
            {
                logger.LogWarning("{Step}: attempt {Attempt} failed, the next one in {Delay} ms: {Error}",
                    step, attempt, (int)delay.TotalMilliseconds, e.Message);
                await Task.Delay(delay, cancellationToken);
                delay = delay * 2 < MaxDelay ? delay * 2 : MaxDelay;
            }
        }
    }

    /// <summary>
    /// The dependency is out of reach, as opposed to a mistake: a wrong password, a broken schema script or a topic
    /// that Kafka refuses to create fail the start at once, as they would on every attempt.
    /// </summary>
    public static bool IsTransient(Exception exception) => exception switch
    {
        NpgsqlException npgsql => npgsql.IsTransient,   // network, DNS, timeouts, "the database system is starting up"
        CreateTopicsException => false,                 // Kafka answered, and the answer is no
        KafkaException => true,                         // no broker within the request timeout
        SocketException or TimeoutException => true,
        _ => false,
    };
}
