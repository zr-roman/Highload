using System.Diagnostics.Metrics;
using System.Text.Json;
using Confluent.Kafka;
using Contracts;
using History.Service.Consuming;
using History.Service.History;
using History.Service.Telemetry;
using History.Service.Tests.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using Payments.Api.Tests.Infrastructure;

namespace History.Service.Tests;

/// <summary>
/// The consumer of the history on a real Kafka and a real MongoDB: every event becomes one document, the offsets are
/// committed after MongoDB has the batch and not before, and a MongoDB that stops answering holds the history back
/// without losing any of it.
/// </summary>
[Collection("Consumer")]
public sealed class HistoryConsumerTests(ConsumerFixture platform)
{
    private static readonly TimeSpan CatchUp = TimeSpan.FromSeconds(30);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Every_Event_Becomes_One_Document_And_The_Offsets_Are_Committed()
    {
        var group = $"history_{Guid.NewGuid():N}";
        var store = platform.Mongo.NewStore(out _);
        await using var consumer = await StartAsync(store, group);

        var captured = Enumerable.Range(0, 20).Select(_ => Captured()).ToList();
        var voided = Enumerable.Range(0, 5).Select(_ => Finalized(PaymentFinalStatus.Voided)).ToList();
        using (var producer = platform.Kafka.NewProducer())
        {
            foreach (var payment in captured)
                await ProduceAsync(producer, payment);
            foreach (var payment in voided)
                await ProduceAsync(producer, payment);
            await ProduceAsync(producer, captured[0]);                       // a repeat, as after a crash of the relay
            await producer.ProduceAsync(Topics.PaymentsFinalized, new Message<string, string> { Key = "42", Value = "not json" }, Ct);
        }

        var ids = captured.Select(p => p.PaymentId).Concat(voided.Select(p => p.PaymentId)).ToHashSet();
        await Eventually.WaitUntilAsync(async () => await CountAsync(store, ids) == ids.Count, CatchUp);
        await Eventually.WaitUntilAsync(async () => await platform.Kafka.UncommittedAsync(group) == 0, CatchUp);

        Assert.Equal(ids.Count, await CountAsync(store, ids));                   // the repeat wrote no second document
    }

    [Fact]
    public async Task While_MongoDB_Does_Not_Answer_Nothing_Is_Committed_And_The_Batch_Is_Written_After()
    {
        var group = $"history_{Guid.NewGuid():N}";
        var store = platform.Mongo.NewStore(out _);
        await using var consumer = await StartAsync(store, group);

        // An empty topic can have zero lag before the consumer has even joined its group.
        var first = Captured();
        using (var producer = platform.Kafka.NewProducer())
            await ProduceAsync(producer, first);
        await Eventually.WaitUntilAsync(async () => await CountAsync(store, [first.PaymentId]) == 1, CatchUp);
        await Eventually.WaitUntilAsync(async () => await platform.Kafka.UncommittedAsync(group) == 0, CatchUp);

        var payments = Enumerable.Range(0, 10).Select(_ => Captured()).ToList();
        await platform.Mongo.PauseAsync();
        try
        {
            using (var producer = platform.Kafka.NewProducer())
                foreach (var payment in payments)
                    await ProduceAsync(producer, payment);

            // Several failed writes, a second or two each: the offsets stay where they were.
            await Task.Delay(TimeSpan.FromSeconds(8), Ct);
            Assert.Equal(payments.Count, await platform.Kafka.UncommittedAsync(group));
        }
        finally
        {
            await platform.Mongo.UnpauseAsync();
        }

        var ids = payments.Select(p => p.PaymentId).ToHashSet();
        await Eventually.WaitUntilAsync(async () => await CountAsync(store, ids) == ids.Count, CatchUp);
        await Eventually.WaitUntilAsync(async () => await platform.Kafka.UncommittedAsync(group) == 0, CatchUp);
    }

    private async Task<RunningConsumer> StartAsync(HistoryStore store, string group)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Kafka"] = platform.Kafka.BootstrapServers })
            .Build();
        var services = new ServiceCollection().AddMetrics().BuildServiceProvider();
        var consumer = new HistoryConsumer(
            configuration,
            Options.Create(new ConsumerOptions { GroupId = group, MaxBatchDelayMs = 20, RetryDelayMs = 200 }),
            store,
            new HistoryMetrics(services.GetRequiredService<IMeterFactory>()),
            NullLogger<HistoryConsumer>.Instance);

        await store.EnsureIndexesAsync(Ct);
        await consumer.StartAsync(Ct);
        return new RunningConsumer(consumer, services);
    }

    private static async Task<long> CountAsync(HistoryStore store, IEnumerable<Guid> ids)
    {
        var filter = Builders<BsonDocument>.Filter.In("_id", ids.Select(id => new BsonBinaryData(id, GuidRepresentation.Standard)));
        return await store.Payments.CountDocumentsAsync(filter, cancellationToken: Ct);
    }

    private static Task ProduceAsync(IProducer<string, string> producer, PaymentCaptured payment) =>
        producer.ProduceAsync(Topics.PaymentsCaptured, new Message<string, string>
        {
            Key = payment.MerchantId.ToString(),
            Value = JsonSerializer.Serialize(payment, ContractsJson.Default.PaymentCaptured),
        }, Ct);

    private static Task ProduceAsync(IProducer<string, string> producer, PaymentFinalized payment) =>
        producer.ProduceAsync(Topics.PaymentsFinalized, new Message<string, string>
        {
            Key = payment.MerchantId.ToString(),
            Value = JsonSerializer.Serialize(payment, ContractsJson.Default.PaymentFinalized),
        }, Ct);

    private static PaymentCaptured Captured() =>
        new(Guid.CreateVersion7(), Random.Shared.Next(1, 50), 150_000, 3_000, "RUB", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddSeconds(-1));

    private static PaymentFinalized Finalized(string status) =>
        new(Guid.CreateVersion7(), Random.Shared.Next(1, 50), 150_000, "RUB", status, DateTimeOffset.UtcNow.AddSeconds(-1), DateTimeOffset.UtcNow);

    private sealed class RunningConsumer(HistoryConsumer consumer, ServiceProvider services) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await consumer.StopAsync(CancellationToken.None);
            consumer.Dispose();
            await services.DisposeAsync();
        }
    }
}
