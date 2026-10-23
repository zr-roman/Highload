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
        try
        {
            await admin.CreateTopicsAsync([new TopicSpecification { Name = topic, NumPartitions = partitions, ReplicationFactor = 1 }]);
            logger.LogInformation("Created Kafka topic {Topic} with {Partitions} partitions", topic, partitions);
        }
        catch (CreateTopicsException e) when (e.Results.All(r => r.Error.Code == ErrorCode.TopicAlreadyExists))
        {
            logger.LogDebug("Kafka topic {Topic} already exists", topic);
        }
    }
}
