using Confluent.Kafka;
using Confluent.Kafka.Admin;
using Contracts;
using Testcontainers.Kafka;

namespace History.Service.Tests.Infrastructure;

/// <summary>
/// Kafka with the two topics the history reads, 8 partitions each, as Payments.Api creates them. The image of the
/// end-to-end tests (PlatformFixture).
/// </summary>
public sealed class KafkaFixture : IAsyncLifetime
{
    private readonly KafkaContainer container = new KafkaBuilder("apache/kafka:4.0.0").Build();

    public string BootstrapServers => container.GetBootstrapAddress();

    public async ValueTask InitializeAsync()
    {
        await container.StartAsync();
        using var admin = new AdminClientBuilder(new AdminClientConfig { BootstrapServers = BootstrapServers }).Build();
        await admin.CreateTopicsAsync(
        [
            new TopicSpecification { Name = Topics.PaymentsCaptured, NumPartitions = Topics.PaymentsCapturedPartitions, ReplicationFactor = 1 },
            new TopicSpecification { Name = Topics.PaymentsFinalized, NumPartitions = Topics.PaymentsFinalizedPartitions, ReplicationFactor = 1 },
        ]);
    }

    public async ValueTask DisposeAsync() => await container.DisposeAsync();

    public IProducer<string, string> NewProducer() =>
        new ProducerBuilder<string, string>(new ProducerConfig { BootstrapServers = BootstrapServers, Acks = Acks.All }).Build();

    /// <summary>
    /// How many messages of the two topics the group has not committed: the end of each partition less its committed
    /// offset, or less nothing where the group has committed none.
    /// </summary>
    public async Task<long> UncommittedAsync(string group)
    {
        using var admin = new AdminClientBuilder(new AdminClientConfig { BootstrapServers = BootstrapServers }).Build();
        var partitions = new[] { (Topics.PaymentsCaptured, Topics.PaymentsCapturedPartitions), (Topics.PaymentsFinalized, Topics.PaymentsFinalizedPartitions) }
            .SelectMany(t => Enumerable.Range(0, t.Item2).Select(p => new TopicPartition(t.Item1, p)))
            .ToList();

        var ends = await admin.ListOffsetsAsync(
            partitions.Select(p => new TopicPartitionOffsetSpec { TopicPartition = p, OffsetSpec = OffsetSpec.Latest() }));
        var committed = (await admin.ListConsumerGroupOffsetsAsync([new ConsumerGroupTopicPartitions(group, partitions)]))
            .Single().Partitions.ToDictionary(p => p.TopicPartition, p => p.Offset.IsSpecial ? 0 : p.Offset.Value);

        return ends.ResultInfos.Sum(end =>
            end.TopicPartitionOffsetError.Offset.Value - committed.GetValueOrDefault(end.TopicPartitionOffsetError.TopicPartition));
    }
}

/// <summary>Kafka and MongoDB together, for the consumer.</summary>
public sealed class ConsumerFixture : IAsyncLifetime
{
    public KafkaFixture Kafka { get; } = new();

    public MongoFixture Mongo { get; } = new();

    public async ValueTask InitializeAsync() => await Task.WhenAll(Kafka.InitializeAsync().AsTask(), Mongo.InitializeAsync().AsTask());

    public async ValueTask DisposeAsync()
    {
        await Kafka.DisposeAsync();
        await Mongo.DisposeAsync();
    }
}

[CollectionDefinition("Consumer")]
public sealed class ConsumerCollection : ICollectionFixture<ConsumerFixture>;
