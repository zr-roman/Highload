using System.Diagnostics;
using Confluent.Kafka;
using Microsoft.Extensions.Options;
using Npgsql;
using OpenTelemetry;
using Payments.Api.Database;
using Payments.Api.Telemetry;

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
    ILogger<OutboxRelay> logger) : BackgroundService
{
    private sealed record OutboxMessage(long Id, string Topic, string Key, string Payload);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await using var _ = await AdvisoryLeaderLock.AcquireAsync(dataSource, AdvisoryLockKeys.OutboxRelay, "Outbox Relay", logger, stoppingToken);

        var minIdleDelay = TimeSpan.FromMilliseconds(options.Value.IdleDelayMs);
        var maxIdleDelay = TimeSpan.FromMilliseconds(options.Value.MaxIdleDelayMs);
        var idleDelay = TimeSpan.Zero;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (await PublishBatchAsync(stoppingToken) > 0)
                {
                    idleDelay = TimeSpan.Zero; // under load the outbox is never empty and the relay never sleeps
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

        if (batch.Count == 0)
            return 0;

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

        await Task.WhenAll(deliveries);

        var produced = Stopwatch.GetElapsedTime(started);

        await DeleteAsync(batch, cancellationToken);
        var deleted = Stopwatch.GetElapsedTime(started);

        // Stage by stage: under load this shows where a cycle spends its time.
        metrics.OutboxBatch(batch.Count, read, produced - read, deleted - produced);
        return batch.Count;
    }

    private async Task<List<OutboxMessage>> ReadBatchAsync(CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand("SELECT id, topic, message_key, payload FROM outbox ORDER BY id LIMIT $1");
        command.Parameters.Add(new NpgsqlParameter<int> { TypedValue = options.Value.BatchSize });

        var batch = new List<OutboxMessage>(options.Value.BatchSize);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
            batch.Add(new OutboxMessage(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)));
        
        return batch;
    }

    private async Task DeleteAsync(List<OutboxMessage> batch, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand("DELETE FROM outbox WHERE id = ANY($1)");
        command.Parameters.Add(new NpgsqlParameter<long[]> { TypedValue = batch.Select(m => m.Id).ToArray() });
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
