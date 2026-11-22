using System.Diagnostics;
using System.Globalization;

namespace SagaDrill;

/// <summary>What a phase found, as far as the list of checks of the report goes.</summary>
public interface IPhaseResult
{
    IReadOnlyList<Check> Checks { get; }
}

/// <summary>
/// What the phases share: a quiet stand to begin and end on, the payments made in between, and the checks of the
/// books of the saga that hold whatever the antifraud did.
/// </summary>
public static class Phase
{
    // The services export their metrics every 15 s (OTEL_METRIC_EXPORT_INTERVAL): a counter read sooner after the
    // last event may not have it yet.
    private static readonly TimeSpan ExportPause = TimeSpan.FromSeconds(25);

    /// <summary>
    /// The start of a phase: a quiet stand, its counters, and the time of the database. From a quiet stand: a payment
    /// made before the phase and finished during it would count in its metrics but not among its payments.
    /// </summary>
    public static async Task<(Counters Before, double From)> BeginAsync(Prometheus prometheus, TimeSpan limit, CancellationToken ct)
    {
        if (await QuietAsync(limit, ct) is null)
            throw new DrillException($"The payments before the phase are still in progress after {limit.TotalSeconds:0} s: " +
                "is recovery running? select status, count(*) from payments join pending_payments using (id) group by 1");
        await Task.Delay(ExportPause, ct);
        return (await Counters.ReadAsync(prometheus, ct), await Postgres.ClockAsync());
    }

    /// <summary>The end of a phase: how long after its load the stand was quiet (null if not within the limit), and the counters then.</summary>
    public static async Task<(TimeSpan? Settled, Counters After)> EndAsync(Prometheus prometheus, TimeSpan limit, CancellationToken ct)
    {
        var settled = await QuietAsync(limit, ct);
        await Task.Delay(ExportPause, ct);
        return (settled, await Counters.ReadAsync(prometheus, ct));
    }

    /// <summary>The payments made from <paramref name="from"/> to <paramref name="to"/>, in SQL.</summary>
    public static string Window(double from, double to) => $"created_at >= {Postgres.Time(from)} and created_at < {Postgres.Time(to)}";

    /// <summary>The payments made from <paramref name="from"/> to <paramref name="to"/>, by status.</summary>
    public static async Task<IReadOnlyDictionary<string, long>> StatusesAsync(double from, double to) =>
        (await Postgres.QueryAsync("payments", $"select status, count(*) from payments where {Window(from, to)} group by status")
            ?? throw new DrillException("Cannot count the payments of the phase in PostgreSQL"))
        .ToDictionary(r => r[0], r => long.Parse(r[1], CultureInfo.InvariantCulture));

    public static Check Settled(IReadOnlyDictionary<string, long> statuses, TimeSpan? settled, TimeSpan limit)
    {
        var inProgress = statuses.GetValueOrDefault("created") + statuses.GetValueOrDefault("authorized");
        return new Check("ни один платёж фазы не остался в работе", inProgress == 0 && settled is not null, settled is { } s
            ? $"все получили итоговый статус через {s.TotalSeconds:0} с после нагрузки"
            : $"через {limit.TotalSeconds:0} с после нагрузки в `created` и `authorized` — {inProgress}");
    }

    /// <summary>
    /// The books of the saga: the metrics and the database tell the same story. A payment is captured after an allow
    /// of the antifraud, in the request or in recovery, or without its decision, failing open: one or the other, once.
    /// More captured than that is a payment that got past the antifraud; fewer, a decision counted twice, as when a
    /// capture failed after the allow and recovery checked again. Every voided payment is counted by
    /// payments.voided, whatever the reason and the path.
    /// </summary>
    public static IEnumerable<Check> Books(SagaCounts counts, IReadOnlyDictionary<string, long> statuses)
    {
        var captured = statuses.GetValueOrDefault("captured");
        var voided = statuses.GetValueOrDefault("voided");
        var checkedOrNot = counts.Allow + counts.FailOpen;
        yield return new Check("каждый проведённый платёж антифрод разрешил или пропустил без проверки", Same(captured, checkedOrNot),
            $"проведено {captured} {(Same(captured, checkedOrNot) ? "=" : "≠")} разрешено {N(counts.Allow)} + без проверки {N(counts.FailOpen)}");
        yield return new Check("метрика отмен сходится с базой", Same(counts.Voided, voided),
            $"`payments.voided` — {N(counts.Voided)}, в базе {voided}");
    }

    /// <summary>
    /// A voided payment moved no money: it has no outbox row, so the Ledger never hears of it. Then the Ledger has
    /// exactly the captured payments, and the balance of <paramref name="merchant"/>, the one the velocity rule
    /// denies, is the sum of his captured ones alone. The Ledger catches up with the outbox in a moment, so the
    /// comparison is repeated until it holds.
    /// </summary>
    public static async Task<Check> LedgerAsync(long merchant, TimeSpan limit, CancellationToken ct)
    {
        const string name = "в журнал попали только проведённые платежи";
        var clock = Stopwatch.StartNew();
        string detail;
        do
        {
            var payments = await Postgres.QueryAsync("payments",
                $"select count(*) filter (where status = 'captured'), " +
                $"coalesce(sum(amount - fee) filter (where status = 'captured' and merchant_id = {merchant}), 0) from payments");
            var processed = await Postgres.NumberAsync("ledger", "select count(*) from processed_payments");
            var balance = await Ledger.BalanceAsync(merchant);
            if (payments is not [[var capturedText, var sumText]] || processed is null)
                return new Check(name, false, "не удалось прочитать базы платежей и журнала");

            var captured = long.Parse(capturedText, CultureInfo.InvariantCulture);
            var sum = long.Parse(sumText, CultureInfo.InvariantCulture);
            detail = $"в журнале {processed} платежей, проведённых в Payments.Api {captured}; баланс продавца {merchant} — " +
                $"{balance?.ToString(CultureInfo.InvariantCulture) ?? "нет"}, сумма его проведённых платежей за вычетом комиссии — {sum}";
            if (processed == captured && balance == sum)
                return new Check(name, true, detail);
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }
        while (clock.Elapsed < limit);
        return new Check(name, false, detail);
    }

    public static async Task<Check> InvariantsAsync()
    {
        var (ok, text) = await Ledger.InvariantsAsync();
        return new Check("инварианты Ledger", ok, ok ? "все платежи сбалансированы, счета сходятся" : text);
    }

    // Counters are whole numbers; a sum of doubles from Prometheus is compared after rounding.
    public static bool Same(double a, double b) => Math.Round(a) == Math.Round(b);

    public static string N(double value) => Math.Round(value).ToString("0", CultureInfo.InvariantCulture);

    public static string Percent(double share) => (share * 100).ToString("0.0", CultureInfo.InvariantCulture).Replace('.', ',') + " %";

    /// <summary>How long until no payment is in progress and the outbox is empty; null if not within the limit.</summary>
    private static async Task<TimeSpan?> QuietAsync(TimeSpan limit, CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < limit)
        {
            if (await Postgres.QueryAsync("payments", "select (select count(*) from pending_payments), (select count(*) from outbox)") is [["0", "0"]])
                return clock.Elapsed;
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }
        return null;
    }
}
