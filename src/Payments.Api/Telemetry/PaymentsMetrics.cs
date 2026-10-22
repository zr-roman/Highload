using System.Diagnostics.Metrics;

namespace Payments.Api.Telemetry;

/// <summary>Business metrics of the service, visible in the dashboard next to the HTTP and SQL metrics.</summary>
public sealed class PaymentsMetrics
{
    public const string MeterName = "Payments.Api";

    private readonly Counter<long> _captured;
    private readonly Counter<long> _capturedAmount;
    private readonly Counter<long> _declined;
    private readonly Counter<long> _replayed;
    private readonly Counter<long> _outboxPublished;
    private readonly Histogram<int> _outboxBatchSize;
    private readonly Histogram<double> _outboxStageDuration;

    public PaymentsMetrics(IMeterFactory meterFactory)
    {
        var meter = meterFactory.Create(MeterName);
        _captured = meter.CreateCounter<long>("payments.captured", description: "Payments approved by the bank");
        _capturedAmount = meter.CreateCounter<long>("payments.captured.amount", unit: "kopecks");
        _declined = meter.CreateCounter<long>("payments.declined", description: "Payments declined by the bank");
        _replayed = meter.CreateCounter<long>("payments.idempotent_replays",
            description: "Requests answered with an existing payment");
        _outboxPublished = meter.CreateCounter<long>("payments.outbox.published",
            description: "Outbox messages delivered to Kafka");
        _outboxBatchSize = meter.CreateHistogram<int>("payments.outbox.batch.size", unit: "{message}",
            description: "Messages moved to Kafka in one relay cycle");
        // Milliseconds: the SDK's default histogram buckets (0 to 10 000) are made for them.
        _outboxStageDuration = meter.CreateHistogram<double>("payments.outbox.stage.duration", unit: "ms",
            description: "One stage of a relay cycle: read the batch, produce it to Kafka, delete it");
    }

    public void Captured(long amount)
    {
        _captured.Add(1);
        _capturedAmount.Add(amount);
    }

    public void Declined() => _declined.Add(1);

    public void Replayed() => _replayed.Add(1);

    /// <summary>One relay cycle: how many messages it moved and how long each stage took.</summary>
    public void OutboxBatch(int count, TimeSpan read, TimeSpan produce, TimeSpan delete)
    {
        _outboxPublished.Add(count);
        _outboxBatchSize.Record(count);
        _outboxStageDuration.Record(read.TotalMilliseconds, new KeyValuePair<string, object?>("stage", "read"));
        _outboxStageDuration.Record(produce.TotalMilliseconds, new KeyValuePair<string, object?>("stage", "produce"));
        _outboxStageDuration.Record(delete.TotalMilliseconds, new KeyValuePair<string, object?>("stage", "delete"));
    }
}
