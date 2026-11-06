using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Contracts;

namespace Ledger.Service.Telemetry;

public sealed class LedgerMetrics
{
    public const string MeterName = "Ledger.Service";

    private readonly Counter<long> _applied;
    private readonly Counter<long> _duplicates;
    private readonly Counter<long> _malformed;
    private readonly Histogram<int> _batchSize;
    private readonly Histogram<double> _batchDuration;
    private readonly Histogram<double> _paymentDelay;

    // Written by the consume loop, read by the metrics exporter on its own thread.
    private readonly ConcurrentDictionary<int, long> _lag = new();

    public LedgerMetrics(IMeterFactory meterFactory)
    {
        var meter = meterFactory.Create(MeterName);
        _applied = meter.CreateCounter<long>("ledger.payments.applied", description: "Payments written to the ledger");
        _duplicates = meter.CreateCounter<long>("ledger.payments.duplicates",
            description: "Redelivered payments skipped by deduplication");
        _malformed = meter.CreateCounter<long>("ledger.messages.malformed");
        _batchSize = meter.CreateHistogram<int>("ledger.batch.size");
        _batchDuration = meter.CreateHistogram<double>("ledger.batch.duration", unit: "ms");
        _paymentDelay = meter.CreateHistogram<double>("ledger.payment.delay", unit: "ms",
            description: "From the capture of a payment in Payments.Api to its write to the ledger");
        meter.CreateObservableGauge("ledger.consumer.lag", ObserveLag, unit: "{message}",
            description: "Messages of a partition that are not in the ledger yet");
    }

    /// <summary>
    /// A batch of one partition is in the ledger. The partition tag shows skew: batches of the partition with a hot merchant
    /// grow while the rest stay small, and in the totals they are lost among the others.
    /// </summary>
    public void BatchWritten(int partition, int size, int applied, TimeSpan duration)
    {
        var tag = new KeyValuePair<string, object?>("partition", partition);
        _batchSize.Record(size, tag);
        _batchDuration.Record(duration.TotalMilliseconds, tag);
        _applied.Add(applied);
        _duplicates.Add(size - applied);
    }

    /// <summary>
    /// The whole way of each payment in one number: the outbox, the relay, Kafka and the batch. CapturedAt comes from
    /// the clock of Payments.Api: on one machine the clocks agree, across machines their difference is part of the delay.
    /// </summary>
    public void PaymentsWritten(int partition, IReadOnlyList<PaymentCaptured> payments, DateTimeOffset writtenAt)
    {
        var tag = new KeyValuePair<string, object?>("partition", partition); // boxed once per batch, not per payment
        foreach (var payment in payments)
            _paymentDelay.Record((writtenAt - payment.CapturedAt).TotalMilliseconds, tag);
    }

    public void MalformedMessage() => _malformed.Add(1);

    public void ConsumerLag(int partition, long messages) => _lag[partition] = messages;

    /// <summary>Revoked partitions belong to another consumer now: their last lag would stay on the chart.</summary>
    public void PartitionsRevoked(IEnumerable<int> partitions)
    {
        foreach (var partition in partitions)
            _lag.TryRemove(partition, out _);
    }

    private IEnumerable<Measurement<long>> ObserveLag() =>
        _lag.Select(lag => new Measurement<long>(lag.Value, new KeyValuePair<string, object?>("partition", lag.Key)));
}
