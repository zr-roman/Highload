using System.Globalization;

namespace SagaDrill;

/// <summary>What the phase found: the load, the payments it made, what the metrics counted, and the checks.</summary>
/// <param name="Statuses">The payments made during the load, by status, as the database has them once the stand settled.</param>
/// <param name="Settled">How long after the load every payment had a final status and the outbox was empty; null if never.</param>
public sealed record DenyResult(
    K6Result K6, IReadOnlyDictionary<string, long> Statuses, IReadOnlyList<(long Merchant, long Voided)> VoidedByMerchant,
    SagaCounts Counts, TimeSpan? Settled, IReadOnlyList<Check> Checks) : IPhaseResult;

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

    public static async Task<DenyResult> RunAsync(DrillOptions options, Prometheus prometheus, string reports, CancellationToken ct)
    {
        var limit = TimeSpan.FromSeconds(options.SettleSeconds);
        var checks = new List<Check>();

        Console.WriteLine("Deny phase: waiting for the payments before it to finish");
        var (before, from) = await Phase.BeginAsync(prometheus, limit, ct);

        Console.WriteLine($"Deny phase: {options.Rate} payments a second for {options.DenySeconds} s");
        var k6 = await K6.RunAsync(options.Rate, options.DenySeconds, reports, "deny", ct);
        var to = await Postgres.ClockAsync();

        Console.WriteLine("Deny phase: waiting for every payment to get its final status");
        var (settled, after) = await Phase.EndAsync(prometheus, limit, ct);
        var counts = SagaCounts.Between(before, after);

        var statuses = await Phase.StatusesAsync(from, to);
        var byMerchant = (await Postgres.QueryAsync("payments",
                $"select merchant_id, count(*) from payments where {Phase.Window(from, to)} and status = 'voided' group by 1 order by 2 desc limit 5") ?? [])
            .Select(r => (long.Parse(r[0], CultureInfo.InvariantCulture), long.Parse(r[1], CultureInfo.InvariantCulture)))
            .ToList();
        var voided = statuses.GetValueOrDefault("voided");

        checks.Add(new Check("антифрод отказывал, и сага отменяла авторизации", counts.Deny > 0 && voided > 0,
            $"отказов {Phase.N(counts.Deny)}, платежей фазы в `voided` — {voided}" +
            (counts.Deny > 0 ? "" : $": правило частоты не сработало — у продавца {HotMerchant} меньше 100 платежей в минуту?")));
        checks.Add(new Check("каждый отказ закончился отменой авторизации", Phase.Same(counts.Deny, counts.VoidsOfDenies),
            $"отказов {Phase.N(counts.Deny)}, отмен после отказа {Phase.N(counts.VoidsOfDenies)}: void {Phase.N(counts.VoidedOnDeny)}, " +
            $"void ответил 404 — {Phase.N(counts.VoidNotFound)}, 409 — {Phase.N(counts.VoidConflict)}"));
        checks.Add(Phase.Settled(statuses, settled, limit));
        checks.Add(new Check("антифрод отвечал: без решения меньше 5 % проверок в запросе",
            counts.ChecksInRequest > 0 && counts.UnknownInRequest / counts.ChecksInRequest < 0.05,
            counts.ChecksInRequest > 0
                ? $"{Phase.Percent(counts.UnknownInRequest / counts.ChecksInRequest)} из {Phase.N(counts.ChecksInRequest)}"
                : "проверок в запросе не было"));
        checks.AddRange(Phase.Books(counts, statuses));
        checks.Add(await Phase.LedgerAsync(HotMerchant, limit, ct));
        checks.Add(await Phase.InvariantsAsync());

        return new DenyResult(k6, statuses, byMerchant, counts, settled, checks);
    }
}
