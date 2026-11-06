using System.Diagnostics.Metrics;

namespace Payments.Api.Telemetry;

/// <summary>Business metrics of the service, visible in the dashboard next to the HTTP and SQL metrics.</summary>
public sealed class PaymentsMetrics
{
    public const string MeterName = "Payments.Api";
    private readonly Counter<long> _captured;
    private readonly Counter<long> _capturedAmount;
    private readonly Counter<long> _declined;
    private readonly Counter<long> _unknown;
    private readonly Counter<long> _replayed;
    private readonly Counter<long> _outboxPublished;
    private readonly Histogram<int> _outboxBatchSize;
    private readonly Histogram<double> _outboxStageDuration;
    private readonly Counter<long> _rateLimited;

    private readonly Counter<long> _rateLimitBypassed;

    private readonly TimeProvider _timeProvider;

    // When the oldest outbox row was written, in UTC ticks, as of the relay's last read; 0 if the outbox was empty.
    // The relay writes it and the metrics exporter reads it on its own thread, hence Volatile: it reads and writes
    // a long in one piece, which plain access guarantees only up to 32 bits.
    private long _outboxOldestTicks;

    public PaymentsMetrics(IMeterFactory meterFactory, TimeProvider timeProvider)
    {
        _timeProvider = timeProvider;
        var meter = meterFactory.Create(MeterName);

        _captured = meter.CreateCounter<long>("payments.captured", description: "Payments approved by the bank");

        _capturedAmount = meter.CreateCounter<long>("payments.captured.amount", unit: "kopecks");

        _declined = meter.CreateCounter<long>("payments.declined", description: "Payments declined by the bank");

        _unknown = meter.CreateCounter<long>("payments.bank_unknown",
            description: "Payments left in 'created' because the bank did not answer; recovery resolves them later");

        _replayed = meter.CreateCounter<long>("payments.idempotent_replays",
            description: "Requests answered with an existing payment");

        _outboxPublished = meter.CreateCounter<long>("payments.outbox.published",
            description: "Outbox messages delivered to Kafka");

        _outboxBatchSize = meter.CreateHistogram<int>("payments.outbox.batch.size", unit: "{message}",
            description: "Messages moved to Kafka in one relay cycle");

        // Milliseconds: the SDK's default histogram buckets (0 to 10 000) are made for them.
        _outboxStageDuration = meter.CreateHistogram<double>("payments.outbox.stage.duration", unit: "ms",
            description: "One stage of a relay cycle: read the batch, produce it to Kafka, delete it");

        _rateLimited = meter.CreateCounter<long>("payments.rate_limited",
            description: "Requests rejected by the merchant rate limit");

        _rateLimitBypassed = meter.CreateCounter<long>("payments.rate_limit.bypassed",
            description: "Requests let through because Redis was unavailable");

        // The age is counted when the exporter asks for it, not when the relay reads: if the relay is stuck on a batch
        // (Kafka is down, the producer keeps retrying), the age keeps growing instead of freezing at its last value.
        meter.CreateObservableGauge("payments.outbox.oldest_age", ObserveOutboxOldestAge, unit: "s",
            description: "How long the oldest message in the outbox has been waiting for the relay");
    }

    public void Captured(long amount)
    {
        _captured.Add(1);
        _capturedAmount.Add(amount);
    }

    public void Declined() => _declined.Add(1);

    public void Replayed() => _replayed.Add(1);

    public void Unknown() => _unknown.Add(1);

    public void RateLimited() => _rateLimited.Add(1);

    public void RateLimitBypassed() => _rateLimitBypassed.Add(1);

    /// <summary>One relay cycle: how many messages it moved and how long each stage took.</summary>
    public void OutboxBatch(int count, TimeSpan read, TimeSpan produce, TimeSpan delete)
    {
        _outboxPublished.Add(count);
        _outboxBatchSize.Record(count);
        _outboxStageDuration.Record(read.TotalMilliseconds, new KeyValuePair<string, object?>("stage", "read"));
        _outboxStageDuration.Record(produce.TotalMilliseconds, new KeyValuePair<string, object?>("stage", "produce"));
        _outboxStageDuration.Record(delete.TotalMilliseconds, new KeyValuePair<string, object?>("stage", "delete"));
    }

    /// <summary>
    /// The relay has read the outbox: <paramref name="createdAt"/> is when its oldest row was written, null if it was empty.
    /// Only the instance that holds the relay lock reads the outbox; the others report 0, so across instances take the maximum.
    /// </summary>
    public void OutboxOldest(DateTimeOffset? createdAt) => Volatile.Write(ref _outboxOldestTicks, createdAt?.UtcTicks ?? 0);

    // created_at comes from the database's clock, the current time from ours. On the stand both are one machine's clock;
    // across machines the age is off by their difference, and the floor at zero keeps a database clock that runs ahead
    // from making it negative.
    private double ObserveOutboxOldestAge()
    {
        var oldest = Volatile.Read(ref _outboxOldestTicks);
        return oldest == 0 ? 0 : Math.Max(0, TimeSpan.FromTicks(_timeProvider.GetUtcNow().UtcTicks - oldest).TotalSeconds);
    }
}
