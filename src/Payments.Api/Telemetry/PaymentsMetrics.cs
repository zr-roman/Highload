using System.Diagnostics.Metrics;
using Payments.Api.Antifraud;
using Payments.Api.Payments;

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
    private readonly Counter<long> _rateLimitLocal;
    private readonly Counter<long> _antifraudDecisions;
    private readonly Counter<long> _failOpen;
    private readonly Counter<long> _voided;
    private readonly Counter<long> _leftInProgress;

    private readonly TimeProvider _timeProvider;

    // When the oldest outbox row was written, in UTC ticks, as of the relay's last read; 0 if the outbox was empty.
    // The relay writes it and the metrics exporter reads it on its own thread, hence Volatile: it reads and writes
    // a long in one piece, which plain access guarantees only up to 32 bits.
    private long _outboxOldestTicks;

    // The same for the oldest payment in progress of each status, as of the last pass of recovery.
    private long _oldestCreatedTicks;
    private long _oldestAuthorizedTicks;

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
            description: "Requests rejected by the merchant rate limit; tags source (redis or local) and quota (default or override)");

        _rateLimitLocal = meter.CreateCounter<long>("payments.rate_limit.local",
            description: "Requests decided by the local limit of the instance because Redis was unavailable");

        // The age is counted when the exporter asks for it, not when the relay reads: if the relay is stuck on a batch
        // (Kafka is down, the producer keeps retrying), the age keeps growing instead of freezing at its last value.
        meter.CreateObservableGauge("payments.outbox.oldest_age", ObserveOutboxOldestAge, unit: "s",
            description: "How long the oldest message in the outbox has been waiting for the relay");

        _antifraudDecisions = meter.CreateCounter<long>("payments.antifraud.decisions",
            description: "Checks of the antifraud in the saga; tags outcome (allow, deny, unknown) and path (request, recovery)");

        _failOpen = meter.CreateCounter<long>("payments.antifraud.fail_open",
            description: "Payments captured without a decision of the antifraud, below Antifraud:FailOpenBelowAmount; request path only");

        _voided = meter.CreateCounter<long>("payments.voided",
            description: "Payments voided; tags reason (antifraud_deny, void_not_found, void_conflict, bank_voided) and path");

        _leftInProgress = meter.CreateCounter<long>("payments.left_in_progress",
            description: "Sagas that stopped short of a final status; tags waits_for (bank, antifraud) and path. " +
                "Includes the bank's silence on the authorization, which payments.bank_unknown counts for the request alone");

        // Counted at export from what the last pass of recovery saw, like the age of the outbox: if recovery stops,
        // the age keeps growing.
        meter.CreateObservableGauge("payments.in_progress.oldest_age", ObserveInProgressOldestAge, unit: "s",
            description: "How long the oldest payment in progress has been waiting, by status (created, authorized)");
    }

    /// <summary>
    /// Every counter of this instance at zero before anything happens, so that each series reaches Prometheus with
    /// the first export. A rate is the difference between two points, and a series that first appears with a burst
    /// already in it has nothing to subtract the burst from. That is how the outbox drained after Kafka came back went
    /// missing from the dashboards: the new relay leader had never published before, and its first point held it all.
    /// Called once the host has started (Program.cs): earlier the OpenTelemetry SDK is not listening yet. Not in the
    /// constructor either: the tests' metric collectors would count the zeros as measurements. Histograms are left
    /// alone: they cannot get a series without a made-up observation.
    /// </summary>
    public void StartCountersAtZero()
    {
        _captured.Add(0);
        _capturedAmount.Add(0);
        _declined.Add(0);
        _unknown.Add(0);
        _replayed.Add(0);
        _outboxPublished.Add(0);
        _rateLimitLocal.Add(0);
        foreach (var source in new[] { "redis", "local" })
        {
            AddRateLimited(0, source, overrideQuota: false);
            AddRateLimited(0, source, overrideQuota: true);
        }

        _failOpen.Add(0);
        foreach (var path in Enum.GetValues<SagaPath>())
        {
            foreach (var outcome in Enum.GetValues<AntifraudOutcome>())
                _antifraudDecisions.Add(0, Tag("outcome", Name(outcome)), Tag("path", Name(path)));

            foreach (var reason in Enum.GetValues<VoidReason>())
                _voided.Add(0, Tag("reason", Name(reason)), Tag("path", Name(path)));

            foreach (var step in new[] { SagaStep.WaitsForBank, SagaStep.WaitsForAntifraud })
                _leftInProgress.Add(0, Tag("waits_for", WaitsFor(step)), Tag("path", Name(path)));
        }
    }

    public void Captured(long amount)
    {
        _captured.Add(1);
        _capturedAmount.Add(amount);
    }

    public void Declined() => _declined.Add(1);

    public void Replayed() => _replayed.Add(1);

    public void Unknown() => _unknown.Add(1);

    /// <summary>
    /// A request got 429; <paramref name="source"/> is who decided: "redis" or "local". The quota tag tells merchants
    /// with a quota of their own from the rest without a merchantId in the metric: 10 000 merchants would be 10 000 series.
    /// </summary>
    public void RateLimited(string source, bool overrideQuota) => AddRateLimited(1, source, overrideQuota);

    private void AddRateLimited(long value, string source, bool overrideQuota) =>
        _rateLimited.Add(value, new KeyValuePair<string, object?>("source", source), new KeyValuePair<string, object?>("quota", overrideQuota ? "override" : "default"));

    public void RateLimitLocal() => _rateLimitLocal.Add(1);

    /// <summary>The antifraud answered a check of the saga, or gave no decision.</summary>
    public void AntifraudDecision(AntifraudOutcome outcome, SagaPath path) =>
        _antifraudDecisions.Add(1, Tag("outcome", Name(outcome)), Tag("path", Name(path)));

    /// <summary>A payment captured without a decision of the antifraud: the risk the platform took on.</summary>
    public void FailOpen() => _failOpen.Add(1);

    public void Voided(VoidReason reason, SagaPath path) => _voided.Add(1, Tag("reason", Name(reason)), Tag("path", Name(path)));

    /// <summary>The saga stopped in 'created' or 'authorized', waiting for the bank or the antifraud: recovery goes on later.</summary>
    public void LeftInProgress(SagaStep step, SagaPath path) =>
        _leftInProgress.Add(1, Tag("waits_for", WaitsFor(step)), Tag("path", Name(path)));

    /// <summary>
    /// Recovery has looked at its queue: when the oldest payment of each status was written, null if there is none.
    /// Only the leader of recovery looks; the others report 0, so across instances take the maximum, as for the outbox.
    /// </summary>
    public void InProgressOldest(DateTimeOffset? created, DateTimeOffset? authorized)
    {
        Volatile.Write(ref _oldestCreatedTicks, created?.UtcTicks ?? 0);
        Volatile.Write(ref _oldestAuthorizedTicks, authorized?.UtcTicks ?? 0);
    }

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
    private double ObserveOutboxOldestAge() => AgeOf(Volatile.Read(ref _outboxOldestTicks));

    private IEnumerable<Measurement<double>> ObserveInProgressOldestAge() =>
    [
        new(AgeOf(Volatile.Read(ref _oldestCreatedTicks)), Tag("status", "created")),
        new(AgeOf(Volatile.Read(ref _oldestAuthorizedTicks)), Tag("status", "authorized")),
    ];

    private double AgeOf(long ticks) =>
        ticks == 0 ? 0 : Math.Max(0, TimeSpan.FromTicks(_timeProvider.GetUtcNow().UtcTicks - ticks).TotalSeconds);

    private static KeyValuePair<string, object?> Tag(string name, string value) => new(name, value);

    private static string Name(SagaPath path) => path switch
    {
        SagaPath.Request => "request",
        SagaPath.Recovery => "recovery",
        _ => throw new ArgumentOutOfRangeException(nameof(path), path, null),
    };

    private static string Name(AntifraudOutcome outcome) => outcome switch
    {
        AntifraudOutcome.Allow => "allow",
        AntifraudOutcome.Deny => "deny",
        AntifraudOutcome.Unknown => "unknown",
        _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, null),
    };

    private static string Name(VoidReason reason) => reason switch
    {
        VoidReason.AntifraudDeny => "antifraud_deny",
        VoidReason.VoidNotFound => "void_not_found",
        VoidReason.VoidConflict => "void_conflict",
        VoidReason.BankVoided => "bank_voided",
        _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, null),
    };

    private static string WaitsFor(SagaStep step) => step switch
    {
        SagaStep.WaitsForBank => "bank",
        SagaStep.WaitsForAntifraud => "antifraud",
        _ => throw new ArgumentOutOfRangeException(nameof(step), step, "not a step that waits"),
    };
}
