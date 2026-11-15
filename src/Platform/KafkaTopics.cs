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
    public static async Task EnsureCreatedAsync(string bootstrapServers, string topic, int partitions, ILogger logger)
    {
        using var admin = new AdminClientBuilder(new AdminClientConfig { BootstrapServers = bootstrapServers }).Build();
        await EnsureCreatedAsync(admin, topic, partitions, logger);
    }

    /// <summary>
    /// The same through an admin client that already exists, such as one that shares a producer's connections
    /// (DependentAdminClientBuilder).
    /// </summary>
    public static async Task EnsureCreatedAsync(IAdminClient admin, string topic, int partitions, ILogger logger)
    {
        try
        {
            // A timeout of its own: by default the request waits up to a minute for a broker that is not there,
            // and the caller (StartupRetry) would rather try again sooner.
            await admin.CreateTopicsAsync(
                [new TopicSpecification { Name = topic, NumPartitions = partitions, ReplicationFactor = 1 }],
                new CreateTopicsOptions { RequestTimeout = TimeSpan.FromSeconds(5) });
            logger.LogInformation("Created Kafka topic {Topic} with {Partitions} partitions", topic, partitions);
        }
        catch (CreateTopicsException e) when (e.Results.All(r => r.Error.Code == ErrorCode.TopicAlreadyExists))
        {
            logger.LogDebug("Kafka topic {Topic} already exists", topic);
        }
    }
}
