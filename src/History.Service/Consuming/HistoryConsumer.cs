using System.Diagnostics;
using System.Text.Json;
using Confluent.Kafka;
using Contracts;
using History.Service.History;
using History.Service.Telemetry;
using Microsoft.Extensions.Options;

namespace History.Service.Consuming;

public sealed class ConsumerOptions
{
    public string GroupId { get; set; } = "history";

    /// <summary>The most documents in one BulkWrite.</summary>
    public int BatchSize { get; set; } = 500;

    /// <summary>The longest a document waits for its batch to fill.</summary>
    public int MaxBatchDelayMs { get; set; } = 50;

    /// <summary>The pause before a batch that failed to write is tried again.</summary>
    public int RetryDelayMs { get; set; } = 1000;
}

/// <summary>
/// Reads payments.captured and payments.finalized in a group of its own, independent of the ledger, and writes them
/// to the history in batches: one BulkWrite of upserts by payment id for a batch, then the commit of its offsets.
/// </summary>
/// <remarks>
/// <para>
/// Offsets are committed only after MongoDB has the batch. A crash in between means the batch is read again, and the
/// upserts write the same documents: at-least-once delivery plus an idempotent write, with no table of processed
/// messages as the ledger has. Order does not matter either: a payment has one event, and it is written whole.
/// </para>
/// <para>
/// One batch across all the partitions of the instance, not one per partition as in the ledger: the documents of
/// different payments do not touch each other, so there is no reason to keep partitions apart.
/// </para>
/// <para>
/// While MongoDB fails, the batch is kept and written again every <see cref="ConsumerOptions.RetryDelayMs"/>, and the
/// partitions are paused: nothing more is fetched, but the consumer keeps polling, so it stays in its group however
/// long MongoDB is away. Once the batch is in, they are resumed and the consumer goes on from where it stopped.
/// </para>
/// </remarks>
public sealed class HistoryConsumer(
    IConfiguration configuration,
    IOptions<ConsumerOptions> options,
    HistoryStore store,
    HistoryMetrics metrics,
    ILogger<HistoryConsumer> logger) : BackgroundService
{
    public static readonly string[] Topics = [global::Contracts.Topics.PaymentsCaptured, global::Contracts.Topics.PaymentsFinalized];

    private static readonly TimeSpan LagInterval = TimeSpan.FromSeconds(1);

    // Accessed only from the consume loop; Kafka rebalance callbacks run inside Consume() on the same thread.
    private readonly List<(TopicPartition Partition, PaymentDocument Document)> _batch = [];
    private readonly Dictionary<TopicPartition, Offset> _next = new();
    private readonly Dictionary<TopicPartition, Offset> _firstUnwritten = new();
    private long _batchStartedAt;
    private long _retryAt;
    private bool _paused;
    private long _lagRecordedAt;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield(); // do not hold up host startup with the consume loop

        var settings = options.Value;
        using var consumer = BuildConsumer(settings);
        consumer.Subscribe(Topics);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                if (Consume(consumer) is { } result)
                    Add(result);

                if (Due(settings))
                    await FlushAsync(consumer, settings, stoppingToken);

                RecordLag(consumer);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutdown. The batch not yet written is not committed and is read again after the restart.
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
            EnableAutoCommit = false,                   // offsets are committed after MongoDB has the batch
            AutoOffsetReset = AutoOffsetReset.Earliest, // a new group starts from the beginning of the topics
            // As in the ledger (PaymentsConsumer): a silent coordinator is given up after 10 s, not 45.
            SessionTimeoutMs = 10_000,
            HeartbeatIntervalMs = 3_000,
        };

        return new ConsumerBuilder<string, string>(config)
            .SetPartitionsAssignedHandler((consumer, partitions) =>
            {
                logger.LogInformation("Assigned partitions: {Partitions}", Describe(partitions));
                // A partition assigned while MongoDB fails waits with the others.
                if (_paused)
                    consumer.Pause(partitions);
            })
            .SetPartitionsRevokedHandler((_, partitions) =>
            {
                // The documents of revoked partitions leave the batch: their offsets were not committed, so the next
                // owner reads them again, and the upsert makes the second write harmless anyway.
                var revoked = partitions.Select(p => p.TopicPartition).ToHashSet();
                _batch.RemoveAll(entry => revoked.Contains(entry.Partition));
                foreach (var partition in revoked)
                {
                    _next.Remove(partition);
                    _firstUnwritten.Remove(partition);
                }
                metrics.PartitionsRevoked(revoked.Select(p => (p.Topic, p.Partition.Value)));

                logger.LogInformation("Revoked partitions: {Partitions}", Describe(partitions.Select(p => p.TopicPartition)));
            })
            .SetErrorHandler((_, error) => logger.LogWarning("Kafka error: {Reason}", error.Reason))
            .Build();
    }

    private static string Describe(IEnumerable<TopicPartition> partitions) =>
        string.Join(", ", partitions.Select(p => $"{p.Topic}[{p.Partition.Value}]"));

    private ConsumeResult<string, string>? Consume(IConsumer<string, string> consumer)
    {
        try
        {
            // Short while a batch is pending, so that its MaxBatchDelay is kept.
            return consumer.Consume(TimeSpan.FromMilliseconds(_next.Count == 0 ? 100 : 5));
        }
        catch (ConsumeException e)
        {
            logger.LogError(e, "Kafka consume failed: {Reason}", e.Error.Reason);
            return null;
        }
    }

    private void Add(ConsumeResult<string, string> result)
    {
        if (_next.Count == 0)
            _batchStartedAt = Stopwatch.GetTimestamp();

        _firstUnwritten.TryAdd(result.TopicPartition, result.Offset);
        _next[result.TopicPartition] = result.Offset + 1;

        try
        {
            _batch.Add((result.TopicPartition, Parse(result.Topic, result.Message.Value)));
        }
        catch (JsonException e)
        {
            // A broken message must not stop its partition: it is logged and skipped, and its offset is committed with
            // the batch. The ledger does the same; a real system would send it to a dead-letter topic.
            logger.LogError(e, "Skipping malformed message at {Offset}", result.TopicPartitionOffset);
            metrics.MalformedMessage();
        }
    }

    /// <summary>The document of an event; JsonException for a message that is not one.</summary>
    public static PaymentDocument Parse(string topic, string? json)
    {
        if (json is null)
            throw new JsonException("Empty message");

        var document = topic == global::Contracts.Topics.PaymentsCaptured
            ? JsonSerializer.Deserialize(json, ContractsJson.Default.PaymentCaptured) is { } captured ? PaymentDocument.From(captured) : null
            : JsonSerializer.Deserialize(json, ContractsJson.Default.PaymentFinalized) is { } finalized ? PaymentDocument.From(finalized) : null;

        // A PaymentCaptured written before stage 5 has no createdAt: the history could not order it. Such messages
        // are gone from the topic within its retention; the backfill from PostgreSQL brings those payments in.
        if (document is null || document.CreatedAt == default)
            throw new JsonException("Not a payment of the history: no payment, or no createdAt");

        return document;
    }

    private bool Due(ConsumerOptions settings)
    {
        if (_next.Count == 0)
            return false;

        if (_retryAt != 0)
            return Stopwatch.GetTimestamp() >= _retryAt;

        return _batch.Count >= settings.BatchSize
            || Stopwatch.GetElapsedTime(_batchStartedAt) >= TimeSpan.FromMilliseconds(settings.MaxBatchDelayMs);
    }

    private async Task FlushAsync(IConsumer<string, string> consumer, ConsumerOptions settings, CancellationToken cancellationToken)
    {
        var documents = _batch.Select(entry => entry.Document).ToList();
        try
        {
            var started = Stopwatch.GetTimestamp();
            await store.WriteAsync(documents, cancellationToken);
            var duration = Stopwatch.GetElapsedTime(started);

            // The offsets only now, once MongoDB has the documents: the next message of each partition of the batch.
            consumer.Commit(_next.Select(entry => new TopicPartitionOffset(entry.Key, entry.Value)));

            metrics.BatchWritten(documents, duration, DateTimeOffset.UtcNow);
            _batch.Clear();
            _next.Clear();
            _firstUnwritten.Clear();
            _retryAt = 0;

            if (_paused)
            {
                consumer.Resume(consumer.Assignment);
                _paused = false;
                logger.LogInformation("The history is written again: {Count} payments, the partitions resumed", documents.Count);
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // MongoDB away, or Kafka refused the commit: the batch stays as it is and is written again, all of it.
            if (!_paused)
            {
                consumer.Pause(consumer.Assignment);
                _paused = true;
            }

            _retryAt = Stopwatch.GetTimestamp() + (long)(settings.RetryDelayMs / 1000.0 * Stopwatch.Frequency);

            logger.LogError(e, "Failed to write a batch of {Count} payments to the history, retrying in {Delay} ms",
                documents.Count, settings.RetryDelayMs);
        }
    }

    /// <summary>
    /// The lag of every assigned partition: its messages that are not in the history yet, from the first one of the
    /// pending batch, or from the read position when nothing is pending, to the end of the partition. Both ends come
    /// from the consumer's own fetch responses, as in the ledger (PaymentsConsumer.RecordLag).
    /// </summary>
    private void RecordLag(IConsumer<string, string> consumer)
    {
        if (Stopwatch.GetElapsedTime(_lagRecordedAt) < LagInterval)
            return;

        _lagRecordedAt = Stopwatch.GetTimestamp();

        foreach (var partition in consumer.Assignment)
        {
            var end = consumer.GetWatermarkOffsets(partition).High;
            var firstUnwritten = _firstUnwritten.TryGetValue(partition, out var first) ? first : consumer.Position(partition);

            // Nothing fetched from the partition since it was assigned: its end and position are not known yet.
            if (end.IsSpecial || firstUnwritten.IsSpecial)
                continue;

            metrics.ConsumerLag(partition.Topic, partition.Partition.Value, Math.Max(0, end.Value - firstUnwritten.Value));
        }
    }
}
