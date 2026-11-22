using System.Diagnostics;
using System.Globalization;

namespace SagaDrill;

/// <summary>What the metrics counted during the phase.</summary>
public sealed record DenyCounts(
    double Allow, double Deny, double Unknown, double VoidedOnDeny, double VoidNotFound, double VoidConflict, double BankVoided, double FailOpen)
{
    /// <summary>Every void that answers a deny: the bank voided, or had nothing to void (404, 409).</summary>
    public double VoidsOfDenies => VoidedOnDeny + VoidNotFound + VoidConflict;

    public double Voided => VoidsOfDenies + BankVoided;
}

/// <summary>What the phase found: the load, the payments it made, what the metrics counted, and the checks.</summary>
/// <param name="Statuses">The payments made during the load, by status, as the database has them once the stand settled.</param>
/// <param name="Settled">How long after the load every payment had a final status and the outbox was empty; null if never.</param>
public sealed record DenyResult(
    K6Result K6, IReadOnlyDictionary<string, long> Statuses, IReadOnlyList<(long Merchant, long Voided)> VoidedByMerchant,
    DenyCounts Counts, TimeSpan? Settled, IReadOnlyList<Check> Checks);

/// <summary>
/// The phase «отказы»: the antifraud says no, and the saga voids the authorization. No special load is needed: in
/// the mix of load/payments.js merchant 1 gets about 10 % of the payments, at 50 a second about 300 a minute, and
/// the velocity rule lets a standard merchant through 100 a minute. The checks: every deny ended in a void, no
/// payment was left in progress, and none of the voided payments reached the Ledger.
/// </summary>
public static class DenyPhase
{
    /// <summary>The merchant the velocity rule denies: the largest one of the mix of load/payments.js.</summary>
    public const long HotMerchant = 1;

    // The services export their metrics every 15 s (OTEL_METRIC_EXPORT_INTERVAL): a counter read sooner after the
    // last event may not have it yet.
    private static readonly TimeSpan ExportPause = TimeSpan.FromSeconds(25);

    public static async Task<DenyResult> RunAsync(DrillOptions options, Prometheus prometheus, string reports, CancellationToken ct)
    {
        var limit = TimeSpan.FromSeconds(options.SettleSeconds);
        var checks = new List<Check>();

        // From a quiet stand: a payment of the warm-up finished during the phase would count in its metrics but not
        // among its payments.
        Console.WriteLine("Deny phase: waiting for the payments of the warm-up to finish");
        if (await QuietAsync(limit, ct) is null)
            throw new DrillException($"The payments of the warm-up are still in progress after {options.SettleSeconds} s: " +
                "is recovery running? select status, count(*) from payments join pending_payments using (id) group by 1");
        await Task.Delay(ExportPause, ct);
        var before = await Counters.ReadAsync(prometheus, ct);
        var from = await Postgres.NowAsync();

        Console.WriteLine($"Deny phase: {options.Rate} payments a second for {options.DenySeconds} s");
        var k6 = await K6.RunAsync(options.Rate, options.DenySeconds, reports, "deny", ct);
        var to = await Postgres.NowAsync();

        Console.WriteLine("Deny phase: waiting for every payment to get its final status");
        var settled = await QuietAsync(limit, ct);
        await Task.Delay(ExportPause, ct);
        var after = await Counters.ReadAsync(prometheus, ct);

        var counts = new DenyCounts(
            Allow: after.Since(before, "decisions", ("outcome", "allow")),
            Deny: after.Since(before, "decisions", ("outcome", "deny")),
            Unknown: after.Since(before, "decisions", ("outcome", "unknown")),
            VoidedOnDeny: after.Since(before, "voided", ("reason", "antifraud_deny")),
            VoidNotFound: after.Since(before, "voided", ("reason", "void_not_found")),
            VoidConflict: after.Since(before, "voided", ("reason", "void_conflict")),
            BankVoided: after.Since(before, "voided", ("reason", "bank_voided")),
            FailOpen: after.Since(before, "fail_open"));
        var requestChecks = after.Since(before, "decisions", ("path", "request"));
        var requestUnknown = after.Since(before, "decisions", ("path", "request"), ("outcome", "unknown"));

        var window = $"created_at >= '{from}' and created_at < '{to}'";
        var statuses = (await Postgres.QueryAsync("payments", $"select status, count(*) from payments where {window} group by status")
                ?? throw new DrillException("Cannot count the payments of the phase in PostgreSQL"))
            .ToDictionary(r => r[0], r => long.Parse(r[1], CultureInfo.InvariantCulture));
        var byMerchant = (await Postgres.QueryAsync("payments",
                $"select merchant_id, count(*) from payments where {window} and status = 'voided' group by 1 order by 2 desc limit 5") ?? [])
            .Select(r => (long.Parse(r[0], CultureInfo.InvariantCulture), long.Parse(r[1], CultureInfo.InvariantCulture)))
            .ToList();
        var voided = statuses.GetValueOrDefault("voided");
        var inProgress = statuses.GetValueOrDefault("created") + statuses.GetValueOrDefault("authorized");

        checks.Add(new Check("антифрод отказывал, и сага отменяла авторизации", counts.Deny > 0 && voided > 0,
            $"отказов {N(counts.Deny)}, платежей фазы в `voided` — {voided}" +
            (counts.Deny > 0 ? "" : $": правило частоты не сработало — у продавца {HotMerchant} меньше 100 платежей в минуту?")));
        checks.Add(new Check("каждый отказ закончился отменой авторизации", Same(counts.Deny, counts.VoidsOfDenies),
            $"отказов {N(counts.Deny)}, отмен после отказа {N(counts.VoidsOfDenies)}: void {N(counts.VoidedOnDeny)}, " +
            $"void ответил 404 — {N(counts.VoidNotFound)}, 409 — {N(counts.VoidConflict)}"));
        checks.Add(new Check("метрика отмен сходится с базой", Same(counts.Voided, voided),
            $"`payments.voided` — {N(counts.Voided)}, в базе {voided}"));
        checks.Add(new Check("ни один платёж фазы не остался в работе", inProgress == 0 && settled is not null, settled is { } s
            ? $"все получили итоговый статус через {s.TotalSeconds:0} с после нагрузки"
            : $"через {options.SettleSeconds} с после нагрузки в `created` и `authorized` — {inProgress}"));
        checks.Add(new Check("антифрод отвечал: без решения меньше 5 % проверок в запросе",
            requestChecks > 0 && requestUnknown / requestChecks < 0.05,
            requestChecks > 0 ? $"{Percent(requestUnknown / requestChecks)} из {N(requestChecks)}" : "проверок в запросе не было"));
        checks.Add(await LedgerAsync(limit, ct));
        var (invariantsOk, invariants) = await Ledger.InvariantsAsync();
        checks.Add(new Check("инварианты Ledger", invariantsOk, invariantsOk ? "все платежи сбалансированы, счета сходятся" : invariants));

        return new DenyResult(k6, statuses, byMerchant, counts, settled, checks);
    }

    // A voided payment moved no money: it has no outbox row, so the Ledger never hears of it. Then the Ledger has
    // exactly the captured payments, and the balance of the merchant the rule denied is the sum of his captured
    // ones alone. The Ledger catches up with the outbox in a moment, so the comparison is repeated until it holds.
    private static async Task<Check> LedgerAsync(TimeSpan limit, CancellationToken ct)
    {
        const string name = "в журнал попали только проведённые платежи";
        var clock = Stopwatch.StartNew();
        string detail;
        do
        {
            var payments = await Postgres.QueryAsync("payments",
                $"select count(*) filter (where status = 'captured'), " +
                $"coalesce(sum(amount - fee) filter (where status = 'captured' and merchant_id = {HotMerchant}), 0) from payments");
            var processed = await Postgres.NumberAsync("ledger", "select count(*) from processed_payments");
            var balance = await Ledger.BalanceAsync(HotMerchant);
            if (payments is not [[var capturedText, var sumText]] || processed is null)
                return new Check(name, false, "не удалось прочитать базы платежей и журнала");

            var captured = long.Parse(capturedText, CultureInfo.InvariantCulture);
            var sum = long.Parse(sumText, CultureInfo.InvariantCulture);
            detail = $"в журнале {processed} платежей, проведённых в Payments.Api {captured}; баланс продавца {HotMerchant} — " +
                $"{balance?.ToString(CultureInfo.InvariantCulture) ?? "нет"}, сумма его проведённых платежей за вычетом комиссии — {sum}";
            if (processed == captured && balance == sum)
                return new Check(name, true, detail);
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }
        while (clock.Elapsed < limit);
        return new Check(name, false, detail);
    }

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

    // Counters are whole numbers; a sum of doubles from Prometheus is compared after rounding.
    private static bool Same(double a, double b) => Math.Round(a) == Math.Round(b);

    private static string N(double value) => Math.Round(value).ToString("0", CultureInfo.InvariantCulture);

    private static string Percent(double share) => (share * 100).ToString("0.0", CultureInfo.InvariantCulture).Replace('.', ',') + " %";
}
