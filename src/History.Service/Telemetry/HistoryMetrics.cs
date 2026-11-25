using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using History.Service.History;

namespace History.Service.Telemetry;

public sealed class HistoryMetrics
{
    public const string MeterName = "History.Service";

    private readonly Counter<long> _written;
    private readonly Counter<long> _malformed;
    private readonly Histogram<int> _batchSize;
    private readonly Histogram<double> _batchDuration;
    private readonly Histogram<double> _lag;

    // Written by the consume loop, read by the metrics exporter on its own thread.
    private readonly ConcurrentDictionary<(string Topic, int Partition), long> _consumerLag = new();

    public HistoryMetrics(IMeterFactory meterFactory)
    {
        var meter = meterFactory.Create(MeterName);
        _written = meter.CreateCounter<long>("history.payments.written", unit: "{payment}",
            description: "Payments written to the history, repeats included, by final status");
        _malformed = meter.CreateCounter<long>("history.messages.malformed", unit: "{message}");
        _batchSize = meter.CreateHistogram<int>("history.batch.size", unit: "{payment}");
        _batchDuration = meter.CreateHistogram<double>("history.batch.duration", unit: "ms");
        _lag = meter.CreateHistogram<double>("history.lag", unit: "ms",
            description: "From the final status of a payment in Payments.Api to its write to the history");
        meter.CreateObservableGauge("history.consumer.lag", ObserveConsumerLag, unit: "{message}",
            description: "Messages of a partition that are not in the history yet");
    }

    /// <summary>
    /// The counters at zero before the first batch, so that their series reach Prometheus with the first export, as
    /// in Ledger (LedgerMetrics.StartCountersAtZero). Called once the host has started.
    /// </summary>
    public void StartCountersAtZero()
    {
        foreach (var status in new[] { PaymentDocument.Captured, "declined", "voided" })
            _written.Add(0, new KeyValuePair<string, object?>("status", status));
        _malformed.Add(0);
    }

    /// <summary>
    /// A batch is in MongoDB. The lag of each payment is measured from its final status, by the clock of the database
    /// of Payments.Api: on one machine the clocks agree, across machines their difference is part of the lag. A repeat,
    /// read again after a failure, counts with its age: after a failure the lag is what the history really was behind.
    /// </summary>
    public void BatchWritten(IReadOnlyCollection<PaymentDocument> documents, TimeSpan duration, DateTimeOffset writtenAt)
    {
        _batchSize.Record(documents.Count);
        _batchDuration.Record(duration.TotalMilliseconds);
        foreach (var document in documents)
        {
            var status = new KeyValuePair<string, object?>("status", document.Status);
            _written.Add(1, status);
            _lag.Record(Math.Max(0, (writtenAt - document.FinalizedAt).TotalMilliseconds), status);
        }
    }

    public void MalformedMessage() => _malformed.Add(1);

    public void ConsumerLag(string topic, int partition, long messages) => _consumerLag[(topic, partition)] = messages;

    /// <summary>A revoked partition is another instance's now: its lag is no longer this one's to report.</summary>
    public void PartitionsRevoked(IEnumerable<(string Topic, int Partition)> partitions)
    {
        foreach (var partition in partitions)
            _consumerLag.TryRemove(partition, out _);
    }

    private IEnumerable<Measurement<long>> ObserveConsumerLag() =>
        _consumerLag.Select(entry => new Measurement<long>(entry.Value,
            new KeyValuePair<string, object?>("topic", entry.Key.Topic),
            new KeyValuePair<string, object?>("partition", entry.Key.Partition)));
}
