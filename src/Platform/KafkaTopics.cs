using Confluent.Kafka;
using Confluent.Kafka.Admin;
using Microsoft.Extensions.Logging;

namespace Platform;

public static class KafkaTopics
{
    /// <summary>
    /// Creates the topic if it does not exist yet. Both services call this at startup,
    /// so it does not matter which one starts first.
    /// </summary>
    public static async Task EnsureCreatedAsync(string bootstrapServers, string topic, int partitions, ILogger logger) =>
        await EnsureCreatedAsync(bootstrapServers, [(topic, partitions)], logger);

    /// <summary>The same for several topics, in one request: for a consumer of more than one topic.</summary>
    public static async Task EnsureCreatedAsync(
        string bootstrapServers, IReadOnlyCollection<(string Topic, int Partitions)> topics, ILogger logger)
    {
        using var admin = new AdminClientBuilder(new AdminClientConfig { BootstrapServers = bootstrapServers }).Build();
        await EnsureCreatedAsync(admin, topics, logger);
    }

    /// <summary>
    /// The same through an admin client that already exists, such as one that shares a producer's connections
    /// (DependentAdminClientBuilder).
    /// </summary>
    public static async Task EnsureCreatedAsync(IAdminClient admin, string topic, int partitions, ILogger logger) =>
        await EnsureCreatedAsync(admin, [(topic, partitions)], logger);

    /// <summary>
    /// Several topics in one request: one wait, not one per topic, when the controller does not answer.
    /// </summary>
    public static async Task EnsureCreatedAsync(
        IAdminClient admin, IReadOnlyCollection<(string Topic, int Partitions)> topics, ILogger logger)
    {
        try
        {
            // A timeout of its own: by default the request waits up to a minute for a broker that is not there,
            // and the caller (StartupRetry) would rather try again sooner.
            // ReplicationFactor -1: the broker's default.replication.factor, 3 in the cluster (deploy/k8s/kafka.yaml),
            // 1 with the single broker of docker-compose and the tests.
            await admin.CreateTopicsAsync(
                topics.Select(t => new TopicSpecification { Name = t.Topic, NumPartitions = t.Partitions, ReplicationFactor = -1 }),
                new CreateTopicsOptions { RequestTimeout = TimeSpan.FromSeconds(5) });

            foreach (var (topic, partitions) in topics)
                logger.LogInformation("Created Kafka topic {Topic} with {Partitions} partitions", topic, partitions);
        }
        catch (CreateTopicsException e) when (e.Results.All(r => r.Error.Code is ErrorCode.NoError or ErrorCode.TopicAlreadyExists))
        {
            // Some of them existed already: the exception carries the outcome of each.
            foreach (var result in e.Results)
            {
                if (result.Error.Code == ErrorCode.NoError)
                    logger.LogInformation("Created Kafka topic {Topic}", result.Topic);
                else
                    logger.LogDebug("Kafka topic {Topic} already exists", result.Topic);
            }
        }
    }
}
