using System.Diagnostics.Metrics;

namespace Ledger.Service.Telemetry;

public sealed class LedgerMetrics
{
    public const string MeterName = "Ledger.Service";

    private readonly Counter<long> _applied;
    private readonly Counter<long> _duplicates;
    private readonly Counter<long> _malformed;
    private readonly Histogram<int> _batchSize;
    private readonly Histogram<double> _batchDuration;

    public LedgerMetrics(IMeterFactory meterFactory)
    {
        var meter = meterFactory.Create(MeterName);
        _applied = meter.CreateCounter<long>("ledger.payments.applied", description: "Payments written to the ledger");
        _duplicates = meter.CreateCounter<long>("ledger.payments.duplicates",
            description: "Redelivered payments skipped by deduplication");
        _malformed = meter.CreateCounter<long>("ledger.messages.malformed");
        _batchSize = meter.CreateHistogram<int>("ledger.batch.size");
        _batchDuration = meter.CreateHistogram<double>("ledger.batch.duration", unit: "ms");
    }

    public void BatchWritten(int size, int applied, TimeSpan duration)
    {
        _batchSize.Record(size);
        _batchDuration.Record(duration.TotalMilliseconds);
        _applied.Add(applied);
        _duplicates.Add(size - applied);
    }

    public void MalformedMessage() => _malformed.Add(1);
}
