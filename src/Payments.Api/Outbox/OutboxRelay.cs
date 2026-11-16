using System.Diagnostics;
using Confluent.Kafka;
using Contracts;
using Microsoft.Extensions.Options;
using Npgsql;
using OpenTelemetry;
using Payments.Api.Database;
using Payments.Api.Telemetry;
using Platform;

namespace Payments.Api.Outbox;

/// <summary>
/// Moves rows from the outbox table to Kafka: read a batch, publish it, delete it.
/// Delivery is at least once: if the process dies after publishing and before deleting,
/// the batch is published again and the Ledger skips the duplicates.
/// </summary>
public sealed class OutboxRelay(
    NpgsqlDataSource dataSource,
    IProducer<string, string> producer,
    IOptions<OutboxOptions> options,
    PaymentsMetrics metrics,
    ILogger<OutboxRelay> logger,
    LeaderDataSource? leaderDataSource = null) : BackgroundService
{
    private sealed record OutboxMessage(long Id, string Topic, string Key, string Payload, DateTimeOffset CreatedAt);

    private bool _topicReady;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await using var _ = await AdvisoryLeaderLock.AcquireAsync(
            leaderDataSource?.Value ?? dataSource, AdvisoryLockKeys.OutboxRelay, "Outbox Relay", logger, stoppingToken);

        var minIdleDelay = TimeSpan.FromMilliseconds(options.Value.IdleDelayMs);
        var maxIdleDelay = TimeSpan.FromMilliseconds(options.Value.MaxIdleDelayMs);
        var busyDelay = TimeSpan.FromMilliseconds(options.Value.BusyDelayMs);
        var idleDelay = TimeSpan.Zero;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (await PublishBatchAsync(stoppingToken) > 0)
                {
                    idleDelay = TimeSpan.Zero; // under load the outbox is never empty and the relay never sleeps

                    // Unless it is told to: BusyDelayMs slows the catch-up down (OutboxOptions).
                    if (busyDelay > TimeSpan.Zero)
                        await Task.Delay(busyDelay, stoppingToken);

                    continue;
                }

                // Nothing to publish: back off 20 ms, 40 ms, 80 ms ... up to 1 s, so an idle service polls
                // the database about once a second instead of 50 times. The price: the first payment after
                // a quiet period reaches Kafka up to MaxIdleDelayMs later.
                idleDelay = idleDelay == TimeSpan.Zero
                    ? minIdleDelay
                    : TimeSpan.FromTicks(Math.Min(idleDelay.Ticks * 2, maxIdleDelay.Ticks));

                await Task.Delay(idleDelay, stoppingToken);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                logger.LogError(e, "Outbox relay failed, retrying in 1 s");
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
            }
        }
    }

    private async Task<int> PublishBatchAsync(CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();

        // Polls are not traced. They run outside any HTTP request, so every poll would become a separate
        // trace with a single SQL span: dozens of empty traces per second that push the useful ones
        // out of the dashboard's buffer.
        List<OutboxMessage> batch;
        using (SuppressInstrumentationScope.Begin())
            batch = await ReadBatchAsync(cancellationToken);

        // The batch is in id order, so its first row has waited the longest. An empty batch: nothing is waiting.
        metrics.OutboxOldest(batch.Count > 0 ? batch[0].CreatedAt : null);

        if (batch.Count == 0)
            return 0;

        // The topic before the first message, or Kafka creates it on its own with a single partition. Here and not at
        // startup: a pod has to start while Kafka is down, and the relay waits for Kafka anyway.
        // One try, and the batch goes out whatever its outcome. CreateTopics goes to the controller the client last
        // heard of, and when that broker's node has just died the request times out again and again until the
        // client's metadata moves on, while the partitions already have live leaders: a new leader of the relay
        // waited 40 s on this check alone, with nothing wrong with the publishing (docs/kafka-failover.md). A topic
        // that is really missing is caught below instead: with auto.create.topics.enable off the producer gets
        // UnknownTopicOrPart, the check is armed again, and the loop above retries.
        if (!_topicReady)
        {
            _topicReady = true;
            try
            {
                using var admin = new DependentAdminClientBuilder(producer.Handle).Build();
                await KafkaTopics.EnsureCreatedAsync(admin, Topics.PaymentsCaptured, Topics.PaymentsCapturedPartitions, logger);
            }
            catch (KafkaException e)
            {
                logger.LogWarning("Could not check the Kafka topic {Topic} ({Reason}): publishing anyway",
                    Topics.PaymentsCaptured, e.Error.Reason);
            }
        }

        var read = Stopwatch.GetElapsedTime(started);

        // A publish that actually moves messages is one span; the DELETE below becomes its child.
        using var activity = PaymentsTelemetry.ActivitySource.StartActivity("outbox publish");
        activity?.SetTag("messaging.destination.name", batch[0].Topic);
        activity?.SetTag("messaging.batch.message_count", batch.Count);

        // No database transaction is open while we wait for Kafka (experiment 2).
        // Messages are handed to the producer in id order; with idempotence enabled the producer keeps
        // that order within a partition. The Ledger does not depend on order anyway: balances are sums.
        var deliveries = new Task<DeliveryResult<string, string>>[batch.Count];
        for (var i = 0; i < batch.Count; i++)
        {
            var message = new Message<string, string> { Key = batch[i].Key, Value = batch[i].Payload };
            deliveries[i] = producer.ProduceAsync(batch[i].Topic, message, cancellationToken);
        }

        try
        {
            await Task.WhenAll(deliveries);
        }
        catch (ProduceException<string, string> e) when (e.Error.Code is ErrorCode.UnknownTopicOrPart or ErrorCode.Local_UnknownTopic)
        {
            _topicReady = false; // the topic is missing after all: create it before the next try
            throw;
        }

        var produced = Stopwatch.GetElapsedTime(started);

        await DeleteAsync(batch, cancellationToken);
        var deleted = Stopwatch.GetElapsedTime(started);

        // Stage by stage: under load this shows where a cycle spends its time.
        metrics.OutboxBatch(batch.Count, read, produced - read, deleted - produced);
        return batch.Count;
    }

    private async Task<List<OutboxMessage>> ReadBatchAsync(CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand("SELECT id, topic, message_key, payload, created_at FROM outbox ORDER BY id LIMIT $1");
        command.Parameters.Add(new NpgsqlParameter<int> { TypedValue = options.Value.BatchSize });

        var batch = new List<OutboxMessage>(options.Value.BatchSize);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
            batch.Add(new OutboxMessage(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.GetFieldValue<DateTimeOffset>(4)));
        
        return batch;
    }

    private async Task DeleteAsync(List<OutboxMessage> batch, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand("DELETE FROM outbox WHERE id = ANY($1)");
        command.Parameters.Add(new NpgsqlParameter<long[]> { TypedValue = batch.Select(m => m.Id).ToArray() });
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
