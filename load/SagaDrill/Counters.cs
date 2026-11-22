namespace SagaDrill;

/// <summary>
/// The counters of the saga at one moment, series by series: per pod and per label set. Two snapshots give what
/// happened between them exactly, not an estimate: increase() of Prometheus extrapolates to the edges of its window.
/// </summary>
public sealed class Counters
{
    // The counters of the saga (Payments.Api/Telemetry/PaymentsMetrics.cs), by the labels the drill compares on.
    private static readonly (string Name, string Query)[] Metrics =
    [
        ("decisions", "sum by (instance, outcome, path) (payments_antifraud_decisions_total)"),
        ("voided", "sum by (instance, reason, path) (payments_voided_total)"),
        ("fail_open", "sum by (instance) (payments_antifraud_fail_open_total)"),
        ("left", "sum by (instance, waits_for, path) (payments_left_in_progress_total)"),
    ];

    private readonly Dictionary<string, Dictionary<string, double>> values;

    private Counters(Dictionary<string, Dictionary<string, double>> values) => this.values = values;

    public static async Task<Counters> ReadAsync(Prometheus prometheus, CancellationToken ct)
    {
        var values = new Dictionary<string, Dictionary<string, double>>();
        foreach (var (name, query) in Metrics)
        {
            var result = await prometheus.QueryAsync(query, ct);
            if (result.Error is { } error)
                throw new DrillException($"Prometheus could not read the counters ({query}): {error}");
            values[name] = result.Series.ToDictionary(
                s => string.Join(",", s.Labels.OrderBy(l => l.Key, StringComparer.Ordinal).Select(l => $"{l.Key}={l.Value}")),
                s => s.Value);
        }
        return new Counters(values);
    }

    /// <summary>
    /// What a counter grew by since <paramref name="before"/>, summed over the series that match the labels. A series
    /// absent before is a pod HPA started since then: its counters began at zero. A value lower than before is a
    /// container that restarted and began again from zero. A pod deleted in between is not counted at all: its series
    /// is still there for 5 minutes, but its values stop.
    /// </summary>
    public double Since(Counters before, string metric, params (string Label, string Value)[] labels) =>
        values[metric]
            .Where(series => labels.All(l => series.Key.Split(',').Contains($"{l.Label}={l.Value}")))
            .Sum(series => before.values[metric].TryGetValue(series.Key, out var was) && was <= series.Value
                ? series.Value - was
                : series.Value);
}

/// <summary>What the counters of the saga grew by during a phase, in both paths unless the name says otherwise.</summary>
/// <param name="ChecksInRequest">Every check of the request path, with a decision or without.</param>
/// <param name="WaitedInRequest">Sagas of the request left in 'authorized' for want of a decision of the antifraud.</param>
/// <param name="WaitedInRecovery">The same in recovery: a payment counts once for each pass that found the antifraud silent.</param>
public sealed record SagaCounts(
    double Allow, double Deny, double Unknown, double ChecksInRequest, double UnknownInRequest, double FailOpen,
    double VoidedOnDeny, double VoidNotFound, double VoidConflict, double BankVoided,
    double WaitedInRequest, double WaitedInRecovery)
{
    /// <summary>Every void that answers a deny: the bank voided, or had nothing to void (404, 409).</summary>
    public double VoidsOfDenies => VoidedOnDeny + VoidNotFound + VoidConflict;

    public double Voided => VoidsOfDenies + BankVoided;

    public double UnknownInRecovery => Unknown - UnknownInRequest;

    public static SagaCounts Between(Counters before, Counters after) => new(
        Allow: after.Since(before, "decisions", ("outcome", "allow")),
        Deny: after.Since(before, "decisions", ("outcome", "deny")),
        Unknown: after.Since(before, "decisions", ("outcome", "unknown")),
        ChecksInRequest: after.Since(before, "decisions", ("path", "request")),
        UnknownInRequest: after.Since(before, "decisions", ("path", "request"), ("outcome", "unknown")),
        FailOpen: after.Since(before, "fail_open"),
        VoidedOnDeny: after.Since(before, "voided", ("reason", "antifraud_deny")),
        VoidNotFound: after.Since(before, "voided", ("reason", "void_not_found")),
        VoidConflict: after.Since(before, "voided", ("reason", "void_conflict")),
        BankVoided: after.Since(before, "voided", ("reason", "bank_voided")),
        WaitedInRequest: after.Since(before, "left", ("waits_for", "antifraud"), ("path", "request")),
        WaitedInRecovery: after.Since(before, "left", ("waits_for", "antifraud"), ("path", "recovery")));
}
