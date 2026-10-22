using System.Diagnostics;
using System.Text.Json;
using Confluent.Kafka;
using Contracts;
using Ledger.Service.Ledger;
using Ledger.Service.Telemetry;
using Microsoft.Extensions.Options;

namespace Ledger.Service.Consuming;

public sealed class ConsumerOptions
{
    public string GroupId { get; set; } = "ledger";
    public int BatchSize { get; set; } = 500;
    public int MaxBatchDelayMs { get; set; } = 20;
}

/// <summary>
/// Reads payments.captured and applies it to the ledger in batches, one batch per partition.
/// Kafka gives each partition to exactly one consumer of the group, so every partition, together with
/// its merchants and its clearing and fee sub-accounts, has a single writer: there are no lock queues
/// on hot rows (experiments 4 and 5). Run a second instance and Kafka splits the partitions between them.
/// </summary>
public sealed class PaymentsConsumer(
    IConfiguration configuration,
    IOptions<ConsumerOptions> options,
    LedgerWriter writer,
    LedgerMetrics metrics,
    ILogger<PaymentsConsumer> logger) : BackgroundService
{
    // Accessed only from the consume loop; Kafka rebalance callbacks run inside Consume() on the same thread.
    private readonly Dictionary<int, PartitionBatch> _batches = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield(); // do not hold up host startup with the consume loop

        var settings = options.Value;
        using var consumer = BuildConsumer(settings);
        consumer.Subscribe(Topics.PaymentsCaptured);
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                // Consume blocks this thread for at most a few milliseconds. That is fine for one consumer
                // per process; with many consumers a dedicated thread per consumer would be the next step.
                var result = Consume(consumer);
                if (result is not null)
                    Add(result);

                await FlushDueBatchesAsync(consumer, settings, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutdown. Unwritten batches are not committed and will be read again after restart.
        }
        finally
        {
            consumer.Close(); // leave the group at once so the partitions are reassigned without waiting
        }
    }

    private IConsumer<string, string> BuildConsumer(ConsumerOptions settings)
    {
        var config = new ConsumerConfig
        {
            BootstrapServers = configuration.GetConnectionString("Kafka")
                ?? throw new InvalidOperationException("Connection string 'Kafka' is not configured."),
            GroupId = settings.GroupId,
            EnableAutoCommit = false,                   // offsets are committed after the database write
            AutoOffsetReset = AutoOffsetReset.Earliest, // a new group starts from the beginning of the topic
        };

        return new ConsumerBuilder<string, string>(config)
            .SetPartitionsAssignedHandler((_, partitions) =>
                logger.LogInformation("Assigned partitions: {Partitions}",
                    string.Join(", ", partitions.Select(p => p.Partition.Value))))
            .SetPartitionsRevokedHandler((_, partitions) =>
            {
                // Unwritten batches of revoked partitions are dropped: their offsets were not committed,
                // so the next owner reads these messages again, and processed_payments skips anything
                // that was already applied.
                foreach (var partition in partitions)
                    _batches.Remove(partition.Partition.Value);

                logger.LogInformation("Revoked partitions: {Partitions}",
                    string.Join(", ", partitions.Select(p => p.Partition.Value)));
            })
            .SetErrorHandler((_, error) => logger.LogWarning("Kafka error: {Reason}", error.Reason))
            .Build();
    }

    private ConsumeResult<string, string>? Consume(IConsumer<string, string> consumer)
    {
        try
        {
            // Short wait while batches are pending, so their MaxBatchDelay is respected.
            return consumer.Consume(TimeSpan.FromMilliseconds(_batches.Count == 0 ? 100 : 5));
        }
        catch (ConsumeException e)
        {
            logger.LogError(e, "Kafka consume failed: {Reason}", e.Error.Reason);
            return null;
        }
    }

    private void Add(ConsumeResult<string, string> result)
    {
        var partition = result.Partition.Value;
        if (!_batches.TryGetValue(partition, out var batch))
            _batches[partition] = batch = new PartitionBatch(partition);

        batch.LastOffset = result.TopicPartitionOffset;
        try
        {
            var payment = result.Message.Value is { } json
                ? JsonSerializer.Deserialize(json, ContractsJson.Default.PaymentCaptured)
                : null;
            if (payment is null)
                throw new JsonException("Empty message");
            batch.Payments.Add(payment);
        }
        catch (JsonException e)
        {
            // A broken message must not block its partition: it is logged and skipped, and its offset is
            // committed with the rest of the batch. A real system would also send it to a dead-letter topic.
            logger.LogError(e, "Skipping malformed message at {Offset}", result.TopicPartitionOffset);
            metrics.MalformedMessage();
        }
    }

    private async Task FlushDueBatchesAsync(
        IConsumer<string, string> consumer, ConsumerOptions settings, CancellationToken cancellationToken)
    {
        if (_batches.Count == 0)
            return;

        var maxDelay = TimeSpan.FromMilliseconds(settings.MaxBatchDelayMs);
        foreach (var batch in _batches.Values.ToList())
        {
            if (batch.Payments.Count < settings.BatchSize && batch.Age < maxDelay)
                continue;

            if (await TryFlushAsync(consumer, batch, cancellationToken))
                _batches.Remove(batch.Partition);
        }
    }

    private async Task<bool> TryFlushAsync(
        IConsumer<string, string> consumer, PartitionBatch batch, CancellationToken cancellationToken)
    {
        try
        {
            var started = Stopwatch.GetTimestamp();
            var applied = batch.Payments.Count == 0
                ? 0
                : await writer.WriteBatchAsync(batch.Partition, batch.Payments, cancellationToken);

            // The offset is committed only after the batch is in the database. A crash in between means the
            // batch is read again, and processed_payments turns the repeat into a no-op: at-least-once delivery
            // plus idempotent processing gives an effectively-once result.
            var last = batch.LastOffset!;
            consumer.Commit([new TopicPartitionOffset(last.TopicPartition, last.Offset + 1)]);

            metrics.BatchWritten(batch.Payments.Count, applied, Stopwatch.GetElapsedTime(started));
            return true;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogError(e, "Failed to write a batch of {Count} payments for partition {Partition}, retrying",
                batch.Payments.Count, batch.Partition);
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            return false;
        }
    }

    private sealed class PartitionBatch(int partition)
    {
        private readonly long _startedAt = Stopwatch.GetTimestamp();

        public int Partition { get; } = partition;
        public List<PaymentCaptured> Payments { get; } = [];
        public TopicPartitionOffset? LastOffset { get; set; }
        public TimeSpan Age => Stopwatch.GetElapsedTime(_startedAt);
    }
}
