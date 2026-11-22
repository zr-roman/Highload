using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SagaDrill;

/// <summary>The moments of the phase, in seconds since 1970 by the clock of the cluster (Postgres.ClockAsync).</summary>
/// <param name="Ready">When every replica of the antifraud was ready again; null if not within the limit.</param>
public sealed record OutageTimes(double LoadStart, double Stop, double Back, double? Ready, double LoadEnd);

/// <summary>An alert of the saga during the phase, in seconds from the stop of the antifraud.</summary>
/// <param name="Resolved">The first evaluation after the last that found it firing; null if it never fired, or fires still.</param>
public sealed record AlertSeen(string Name, double? Pending, double? Firing, double? Resolved, bool FiringAtEnd);

/// <summary>The payments too large to fail open made while the antifraud was away, by what became of them.</summary>
/// <param name="CapturedBeforeBack">Captured before the antifraud was back: past it, without its decision.</param>
public sealed record LargePayments(long Made, long CapturedBeforeBack, long Captured, long Voided, long Declined, long InProgress);

/// <summary>
/// How recovery took up the payments that waited, once the antifraud was back. Recovery stops a pass and pauses for
/// 10 s while the circuit to the antifraud is open; a check without a decision otherwise holds back its own payment
/// alone (PaymentRecovery). If pauses slow it, its decisions come in bursts with gaps of 10 s; if its own pace does,
/// they come evenly.
/// </summary>
/// <param name="Points">Every 15 s from the return to the end of the phase: seconds from the stop, decisions a second,
/// checks without a decision a second, the oldest payment in 'authorized' in seconds, the antifraud's p50 and p99 in ms.</param>
/// <param name="UnknownWhileDown">Checks of recovery without a decision while the antifraud was away; after it, below.</param>
/// <param name="Drained">When no payment in 'authorized' was older than a minute, in seconds from the stop; null if never.</param>
public sealed record RecoveryDrain(
    IReadOnlyList<(double At, double Decisions, double Unknown, double Oldest, double P50, double P99)> Points,
    double? UnknownWhileDown, double? UnknownAfter, double? Drained);

/// <summary>What the phase found: the load, when the alerts went off, the payments it made, the counters, the checks.</summary>
/// <param name="LongestWait">The longest wait in 'authorized' Prometheus saw, and when, in seconds from the stop.</param>
/// <param name="Carried">
/// The oldest payment made before the stop that waited out the outage, in seconds from the stop (below 0); null if
/// none. See <see cref="OutagePhase.CarriedAsync"/>.
/// </param>
public sealed record OutageResult(
    K6Result K6, OutageTimes Times, long FailOpenBelow, double Interval, IReadOnlyList<AlertSeen> Alerts,
    (double Seconds, double At)? LongestWait, IReadOnlyDictionary<string, long> Statuses, SagaCounts Counts,
    LargePayments Large, TimeSpan? Settled, IReadOnlyList<Check> Checks, RecoveryDrain? Drain = null, double? Carried = null) : IPhaseResult;

/// <summary>
/// The phase «антифрод лежит»: under load the antifraud goes away, and comes back. A payment the bank approved and
/// the antifraud did not answer is captured unchecked if it is small (fail open, the request path only) and waits in
/// 'authorized' otherwise; recovery checks it again once the antifraud is back. Meanwhile on-call must hear of it:
/// PaymentsStuckInAuthorized and AntifraudFailingOpen fire while the antifraud is away, and resolve after.
/// </summary>
public static partial class OutagePhase
{
    public const string StuckInAuthorized = "PaymentsStuckInAuthorized";
    public const string FailingOpen = "AntifraudFailingOpen";
    public const string StuckInCreated = "PaymentsStuckInCreated";

    /// <summary>Where the stand takes Antifraud:FailOpenBelowAmount from: the settings, unless the manifest sets it.</summary>
    public static readonly string[] Files = ["src/Payments.Api/appsettings.json", "deploy/k8s/payments-api.yaml"];

    private const string Antifraud = "antifraud";

    // The alerts of the saga in the order of the report: the two the outage must fire, then the one it must not.
    private static readonly string[] Names = [StuckInAuthorized, FailingOpen, StuckInCreated];

    /// <summary>
    /// kubectl scale returns before the pods of the antifraud have shut down, and they may still answer a check that
    /// reached them. A payment made later than this after the stop had no antifraud to answer it.
    /// </summary>
    public static readonly TimeSpan ShutDown = TimeSpan.FromSeconds(10);

    // The stop is read off the database after kubectl scale has returned, a second or so after the antifraud began
    // to go: an alert may look a little sooner than it can fire.
    private const double Slack = 5;

    public static async Task<OutageResult> RunAsync(DrillOptions options, Prometheus prometheus, string reports, CancellationToken ct)
    {
        var limit = TimeSpan.FromSeconds(options.SettleSeconds);
        var failOpenBelow = FailOpenBelow();
        var reach = RecoveryReach();
        if (failOpenBelow == 0)
            throw new DrillException("Antifraud:FailOpenBelowAmount is 0 on the stand: fail open is off, and the outage phase " +
                "checks what the saga does with it");
        var group = await prometheus.AlertRulesAsync(Alerts.Group, ct)
            ?? throw new DrillException($"Prometheus has no group {Alerts.Group}");

        Console.WriteLine("Outage phase: waiting for the payments before it to finish");
        var (before, loadStart) = await Phase.BeginAsync(prometheus, limit, ct);
        var (antifraud, error) = await Stand.DeploymentAsync(Antifraud);
        if (antifraud is not { Desired: > 0 })
            throw new DrillException($"The antifraud has no replicas to stop: {(antifraud is null ? error : "0 of 0")}");
        var autoSync = await Stand.AutoSyncAsync()
            ?? throw new DrillException("The auto-sync of Argo CD is off already: put it back, kubectl apply -f deploy/argocd/highload.yaml");

        var seconds = options.BeforeSeconds + options.DownSeconds + options.AfterSeconds;
        Console.WriteLine($"Outage phase: {options.Rate} payments a second for {seconds} s, " +
            $"the antifraud away from {options.BeforeSeconds} s to {options.BeforeSeconds + options.DownSeconds} s");
        // Argo CD would put the replicas back within seconds: selfHeal is on, the preflight made sure of it.
        await Stand.SetAutoSyncAsync(null);
        OutageTimes times;
        K6Result k6;
        try
        {
            (times, k6) = await LoadAsync(options, loadStart, antifraud.Desired, autoSync, limit, reports, ct);
        }
        catch
        {
            // Ctrl+C, or a step that failed halfway: the stand goes back as it was all the same, before anything else.
            await RestoreAsync(antifraud.Desired, autoSync);
            throw;
        }

        Console.WriteLine("Outage phase: waiting for every payment to get its final status");
        var (settled, after) = await Phase.EndAsync(prometheus, limit, ct);
        var counts = SagaCounts.Between(before, after);
        Console.WriteLine("Outage phase: waiting for the alerts to resolve and for Argo CD to sync");
        var (firing, firingError) = await ResolvedAsync(prometheus, limit, ct);
        var (argo, argoError) = await ArgoBackAsync(limit, ct);
        var end = await Postgres.ClockAsync();

        var alerts = await AlertsAsync(prometheus, times, end, group.Interval, firing, ct);
        var longest = await LongestWaitAsync(prometheus, times, end, ct);
        var drain = await DrainAsync(prometheus, times, end, ct);
        var statuses = await Phase.StatusesAsync(times.LoadStart, times.LoadEnd);
        var large = await LargeAsync(times, failOpenBelow);
        var carried = await CarriedAsync(times);

        var checks = new List<Check>();
        var rules = group.Rules.ToDictionary(r => r.Name);
        AlertSeen Seen(string name) => alerts.Single(a => a.Name == name);
        checks.Add(Fired(Seen(StuckInAuthorized), rules.GetValueOrDefault(StuckInAuthorized), times, (carried, reach)));
        checks.Add(Fired(Seen(FailingOpen), rules.GetValueOrDefault(FailingOpen), times, age: null));
        checks.Add(Silent(Seen(StuckInCreated), rules.ContainsKey(StuckInCreated)));
        checks.Add(Resolved(alerts, times, firing, firingError, limit));
        checks.Add(FailedOpenSmall(counts, large, failOpenBelow));
        checks.Add(Phase.Settled(statuses, settled, limit));
        checks.AddRange(Phase.Books(counts, statuses));
        checks.Add(await Phase.LedgerAsync(DenyPhase.HotMerchant, limit, ct));
        checks.Add(await Phase.InvariantsAsync());
        checks.Add(StandBack(times, antifraud.Desired, argo, argoError, limit));

        return new OutageResult(k6, times, failOpenBelow, group.Interval, alerts, longest, statuses, counts, large, settled, checks, drain,
            carried);
    }

    // The load runs from end to end, the antifraud stopped and started under it. On the normal path the stand is put
    // back here, as soon as the antifraud is ready: the rest of the load runs with Argo CD watching again.
    private static async Task<(OutageTimes, K6Result)> LoadAsync(
        DrillOptions options, double loadStart, int replicas, string autoSync, TimeSpan limit, string reports, CancellationToken ct)
    {
        using var stopLoad = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var load = K6.RunAsync(options.Rate, options.BeforeSeconds + options.DownSeconds + options.AfterSeconds, reports, "outage", stopLoad.Token);
        try
        {
            await DuringLoadAsync(load, TimeSpan.FromSeconds(options.BeforeSeconds), ct);
            await Stand.ScaleAsync(Antifraud, 0);
            var down = Stopwatch.StartNew();
            var stop = await Postgres.ClockAsync();
            Console.WriteLine($"Outage phase: the antifraud is stopped, back in {options.DownSeconds} s. Ctrl+C puts the stand back; " +
                $"if the tool is killed, put it back by hand: kubectl -n {Stand.Namespace} scale deployment/{Antifraud} --replicas={replicas}; " +
                "kubectl apply -f deploy/argocd/highload.yaml");

            await DuringLoadAsync(load, TimeSpan.FromSeconds(options.DownSeconds) - down.Elapsed, ct);
            await Stand.ScaleAsync(Antifraud, replicas);
            var back = await Postgres.ClockAsync();
            Console.WriteLine("Outage phase: the antifraud is back, waiting for its replicas to be ready");
            var ready = await ReadyAsync(replicas, limit, ct);
            await Stand.SetAutoSyncAsync(autoSync);
            Console.WriteLine("Outage phase: the auto-sync of Argo CD is back on; the load goes on");

            var k6 = await load;
            return (new OutageTimes(loadStart, stop, back, ready, await Postgres.ClockAsync()), k6);
        }
        finally
        {
            // The phase failed or was stopped while k6 ran: k6 stops too.
            if (!load.IsCompleted)
            {
                stopLoad.Cancel();
                try
                {
                    await load;
                }
                catch (OperationCanceledException)
                {
                }
            }
        }
    }

    // A wait during the load. If k6 ends first, its error is the one to report, not a phase cut short.
    private static async Task DuringLoadAsync(Task<K6Result> load, TimeSpan time, CancellationToken ct)
    {
        var wait = Task.Delay(time > TimeSpan.Zero ? time : TimeSpan.Zero, ct);
        if (await Task.WhenAny(load, wait) == load)
        {
            await load;
            throw new DrillException("k6 ended before the outage was over: see outage-k6.txt");
        }
        await wait;
    }

    // When every replica of the antifraud is ready again, by the clock of the database; null if not within the limit.
    private static async Task<double?> ReadyAsync(int replicas, TimeSpan limit, CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < limit)
        {
            if (await Stand.DeploymentAsync(Antifraud) is ({ } now, _) && now.Ready == replicas)
                return await Postgres.ClockAsync();
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }
        return null;
    }

    // Never throws, and says plainly when it fails: a stand left with no antifraud and no auto-sync fails every
    // preflight after it.
    private static async Task RestoreAsync(int replicas, string autoSync)
    {
        var failed = new List<string>();
        foreach (var step in new Func<Task>[] { () => Stand.ScaleAsync(Antifraud, replicas), () => Stand.SetAutoSyncAsync(autoSync) })
        {
            try
            {
                await step();
            }
            catch (DrillException e)
            {
                failed.Add(e.Message);
            }
        }
        if (failed.Count == 0)
            Console.WriteLine($"Outage phase: the stand is put back: {replicas} replicas of the antifraud, the auto-sync of Argo CD on");
        else
            Console.Error.WriteLine($"Outage phase: the stand is NOT put back ({string.Join("; ", failed)}). By hand: " +
                $"kubectl -n {Stand.Namespace} scale deployment/{Antifraud} --replicas={replicas}; kubectl apply -f deploy/argocd/highload.yaml");
    }

    // The alerts of the saga still firing once the limit is out, or none; the error if Prometheus cannot say.
    // AntifraudFailingOpen looks at 2 minutes of checks: it resolves once they hold no more fail opens than 5 %.
    private static async Task<(IReadOnlyList<string> Firing, string? Error)> ResolvedAsync(
        Prometheus prometheus, TimeSpan limit, CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();
        while (true)
        {
            var result = await prometheus.QueryAsync($"ALERTS{{alertname=~\"{string.Join("|", Names)}\", alertstate=\"firing\"}}", ct);
            if (result.Error is { } error)
                return ([], error);
            var firing = result.Series.Select(s => s.Labels.GetValueOrDefault("alertname", "")).Distinct().ToList();
            if (firing.Count == 0 || clock.Elapsed >= limit)
                return (firing, null);
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
        }
    }

    private static async Task<(ArgoApp? App, string Error)> ArgoBackAsync(TimeSpan limit, CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();
        while (true)
        {
            var (app, error) = await Stand.ArgoAppAsync();
            if (app is { Sync: "Synced", Health: "Healthy", SelfHeal: true } || clock.Elapsed >= limit)
                return (app, error);
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
        }
    }

    // From ALERTS, the series the rules write at each evaluation: pending while the condition holds for less than its
    // for, firing after. A resolved alert gets no more samples, so it resolved at the evaluation after its last one.
    private static async Task<IReadOnlyList<AlertSeen>> AlertsAsync(
        Prometheus prometheus, OutageTimes times, double end, double interval, IReadOnlyList<string> firing, CancellationToken ct)
    {
        var result = await prometheus.SamplesAsync($"ALERTS{{alertname=~\"{string.Join("|", Names)}\"}}", times.LoadStart, end, ct);
        if (result.Error is { } error)
            throw new DrillException($"Prometheus could not give the samples of ALERTS: {error}");

        double? First(string name, string state) => Points(name, state).Select(p => (double?)p.Time).Min() - times.Stop;
        IEnumerable<(double Time, double Value)> Points(string name, string state) => result.Series
            .Where(s => s.Labels.GetValueOrDefault("alertname") == name && s.Labels.GetValueOrDefault("alertstate") == state)
            .SelectMany(s => s.Points);

        return Names.Select(name =>
        {
            var last = Points(name, "firing").Select(p => (double?)p.Time).Max();
            var stillFiring = firing.Contains(name);
            return new AlertSeen(name, First(name, "pending"), First(name, "firing"),
                last is { } t && !stillFiring ? t + interval - times.Stop : null, stillFiring);
        }).ToList();
    }

    // Read right after the phase: Prometheus keeps its history for hours at best. Nothing here is a check, the report
    // shows it; null if Prometheus has none of it.
    private static async Task<RecoveryDrain?> DrainAsync(Prometheus prometheus, OutageTimes times, double end, CancellationToken ct)
    {
        const double step = 15;
        const string decisions = "payments_antifraud_decisions_total{path=\"recovery\"";
        const string attempts = "http_client_request_duration_seconds_bucket{job=\"payments-api\", server_address=\"antifraud\"}";
        string[] queries =
        [
            $"sum(rate({decisions}, outcome=~\"allow|deny\"}}[45s]))",
            $"sum(rate({decisions}, outcome=\"unknown\"}}[45s]))",
            "max(payments_in_progress_oldest_age_seconds{status=\"authorized\"})",
            $"1000 * histogram_quantile(0.5, sum by (le) (rate({attempts}[45s])))",
            $"1000 * histogram_quantile(0.99, sum by (le) (rate({attempts}[45s])))",
        ];
        var series = new List<Dictionary<double, double>>();
        foreach (var query in queries)
        {
            var (points, error) = await prometheus.RangeAsync(query, times.Back, end, step, ct);
            if (error is not null)
                return null;
            series.Add(points.ToDictionary(p => p.Time, p => p.Value));
        }
        if (series[0].Count == 0)
            return null;

        double At(int i, double time) => series[i].TryGetValue(time, out var value) ? value : double.NaN;
        var rows = series[0].Keys.Order()
            .Select(time => (time - times.Stop, At(0, time), At(1, time), At(2, time), At(3, time), At(4, time)))
            .ToList();
        var ready = times.Ready ?? times.Back;
        var drained = rows.FirstOrDefault(r => r.Item1 >= ready - times.Stop && r.Item4 < 60);

        async Task<double?> UnknownAsync(double from, double to)
        {
            var result = await prometheus.QueryAsync(
                $"sum(increase({decisions}, outcome=\"unknown\"}}[{Math.Max(1, (int)Math.Round(to - from))}s] @ {to.ToString("0.###", CultureInfo.InvariantCulture)}))", ct);
            return result.Series is [{ Value: var value }] && !double.IsNaN(value) ? value : null;
        }

        return new RecoveryDrain(rows, await UnknownAsync(times.Stop, times.Back), await UnknownAsync(times.Back, end),
            drained == default ? null : drained.Item1);
    }

    // The gauge only the leader of recovery reports; the other pods report 0.
    private static async Task<(double, double)?> LongestWaitAsync(Prometheus prometheus, OutageTimes times, double end, CancellationToken ct)
    {
        var result = await prometheus.SamplesAsync("payments_in_progress_oldest_age_seconds{status=\"authorized\"}", times.LoadStart, end, ct);
        var points = result.Series.SelectMany(s => s.Points).ToList();
        if (points.Count == 0)
            return null;
        var longest = points.MaxBy(p => p.Value);
        return (longest.Value, longest.Time - times.Stop);
    }

    private static async Task<LargePayments> LargeAsync(OutageTimes times, long failOpenBelow)
    {
        var made = Phase.Window(times.Stop + ShutDown.TotalSeconds, times.Back);
        var back = Postgres.Time(times.Back);
        var rows = await Postgres.QueryAsync("payments",
            $"select count(*), count(*) filter (where status = 'captured' and updated_at < {back}), " +
            "count(*) filter (where status = 'captured'), count(*) filter (where status = 'voided'), " +
            "count(*) filter (where status = 'declined'), count(*) filter (where status in ('created', 'authorized')) " +
            $"from payments where {made} and amount >= {failOpenBelow}");
        if (rows is not [{ Length: 6 } row])
            throw new DrillException("Cannot count the large payments of the outage in PostgreSQL");
        var n = row.Select(v => long.Parse(v, CultureInfo.InvariantCulture)).ToArray();
        return new LargePayments(n[0], n[1], n[2], n[3], n[4], n[5]);
    }

    /// <summary>
    /// The oldest payment made before the stop that was still in progress when the antifraud had been gone for
    /// <see cref="ShutDown"/>: it waited out the outage, and the age of PaymentsStuckInAuthorized counts from it. In
    /// seconds from the stop; null if there was none. One check of the request without a decision leaves a large
    /// payment in 'authorized', and recovery takes it up only after StaleAfter: made shortly before the stop, it finds
    /// the antifraud gone.
    /// </summary>
    public static async Task<double?> CarriedAsync(OutageTimes times)
    {
        var rows = await Postgres.QueryAsync("payments",
            $"select extract(epoch from min(created_at)) from payments where {Phase.Window(times.LoadStart, times.Stop)} " +
            $"and status in ('captured', 'voided') and updated_at > {Postgres.Time(times.Stop + ShutDown.TotalSeconds)}");
        if (rows is null)
            throw new DrillException("Cannot find the payments made before the stop of the antifraud in PostgreSQL");
        // None: min() is null, and psql prints an empty line, which QueryAsync leaves out.
        return rows is [[var value, ..], ..] && value.Length > 0 ? double.Parse(value, CultureInfo.InvariantCulture) - times.Stop : null;
    }

    // It cannot fire sooner than its condition can hold for its for. An age above its threshold needs that many seconds
    // of the antifraud away, or of a payment made before the stop that waited it out (Carried): one made up to the
    // reach of recovery before it may, since recovery takes a payment up only after StaleAfter, in a pass every
    // Interval. Older, recovery had the antifraud up to check it: it waited for something else, and the drill
    // measured that. Without age, an alert of a rate needs its for alone.
    private static Check Fired(AlertSeen alert, LoadedRule? rule, OutageTimes times, (double? Carried, double Reach)? age)
    {
        var name = $"{alert.Name} сработал, пока антифрод лежал";
        if (rule is null)
            return new Check(name, false, $"такого алерта в группе `{Alerts.Group}` в Prometheus нет");

        var threshold = age is not null && Alerts.Split(rule.Query) is var (_, text) && double.TryParse(
            text.Split(' ')[^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ? number : 0;
        var carried = Math.Min(0, age?.Carried ?? 0);
        var earliest = threshold + rule.Duration + carried;
        var away = times.Back - times.Stop;
        var why = age is null ? $"for {S(rule.Duration)}"
            : carried < 0 ? $"порог {S(threshold)} и for {S(rule.Duration)} от самого старого платежа, переждавшего аварию: создан за {S(-carried)} до остановки"
            : $"порог {S(threshold)} и for {S(rule.Duration)}";
        if (alert.Firing is not { } firing)
            return new Check(name, false, $"не сработал за {S(away)}, пока антифрод лежал" +
                (alert.Pending is { } pending ? $"; pending с {At(pending)}" : ": и pending не был"));

        var detail = $"firing через {S(firing)} после остановки, антифрод вернули через {S(away)}";
        if (age is { Reach: var reach } && carried < -reach - Slack)
            return new Check(name, false, detail + $"; платёж, созданный за {S(-carried)} до остановки, ждал до конца аварии: " +
                $"восстановление берёт платёж через {S(reach)} и могло проверить его, пока антифрод отвечал — он стоял не из-за аварии");

        var early = firing < earliest - Slack;
        return new Check(name, !early && firing <= away, detail +
            (early
                ? $"; раньше {S(earliest)} сработать не мог ({why}): что-то стояло и до остановки"
                : $"; раньше {S(earliest)} не мог: {why}"));
    }

    private static Check Silent(AlertSeen alert, bool loaded) =>
        new($"{alert.Name} молчал: банк отвечал", loaded && alert.Firing is null, !loaded
            ? $"такого алерта в группе `{Alerts.Group}` в Prometheus нет"
            : alert.Firing is { } firing ? $"firing с {At(firing)}"
            : alert.Pending is { } pending ? $"не срабатывал, но был pending с {At(pending)}" : "не срабатывал и не был pending");

    private static Check Resolved(IReadOnlyList<AlertSeen> alerts, OutageTimes times, IReadOnlyList<string> firing, string? error, TimeSpan limit)
    {
        const string name = "алерты погасли, когда антифрод вернулся";
        if (error is not null)
            return new Check(name, false, $"Prometheus не ответил: {error}");
        if (firing.Count > 0)
            return new Check(name, false, $"через {limit.TotalSeconds:0} с после нагрузки ещё горят: {string.Join(", ", firing)}");
        var back = times.Back - times.Stop;
        var resolved = alerts.Where(a => a.Resolved is not null).Select(a => $"{a.Name} — через {S(a.Resolved!.Value - back)}").ToList();
        return new Check(name, true, resolved.Count > 0
            ? $"после возвращения антифрода погасли {string.Join(", ", resolved)}"
            : "ни один алерт саги не горит");
    }

    private static Check FailedOpenSmall(SagaCounts counts, LargePayments large, long failOpenBelow) =>
        new($"без проверки проходили только платежи меньше {Roubles(failOpenBelow)}, остальные ждали антифрод",
            counts.FailOpen > 0 && counts.WaitedInRequest > 0 && large.CapturedBeforeBack == 0,
            $"без проверки проведено {Phase.N(counts.FailOpen)}, ждали в `authorized` {Phase.N(counts.WaitedInRequest)}; " +
            $"из {large.Made} платежей от {Roubles(failOpenBelow)}, созданных пока антифрод лежал, до его возвращения проведено {large.CapturedBeforeBack}");

    private static Check StandBack(OutageTimes times, int replicas, ArgoApp? argo, string argoError, TimeSpan limit)
    {
        var ok = times.Ready is not null && argo is { Sync: "Synced", Health: "Healthy", SelfHeal: true };
        var antifraud = times.Ready is { } ready
            ? $"антифрод готов через {S(ready - times.Back)} после возвращения, {replicas} из {replicas}"
            : $"реплики антифрода не готовы и через {S(limit.TotalSeconds)} после возвращения";
        var app = argo is null
            ? $"приложение Argo CD не прочитать: {argoError}"
            : $"Argo CD: {argo.Sync}, {argo.Health}, selfHeal {(argo.SelfHeal ? "включён" : "выключен")}";
        return new Check("стенд вернулся как был: реплики антифрода готовы, Argo CD синхронизирован и сам возвращает ручные правки", ok,
            $"{antifraud}; {app}");
    }

    // As the stand runs it: appsettings.json of Payments.Api, unless the manifest sets the variable.
    private static long FailOpenBelow()
    {
        var env = FailOpenVariable().Match(File.ReadAllText(Files[1]));
        if (env.Success)
            return long.Parse(env.Groups["value"].Value, CultureInfo.InvariantCulture);

        using var json = JsonDocument.Parse(File.ReadAllText(Files[0]),
            new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        return json.RootElement.GetProperty("Antifraud").GetProperty("FailOpenBelowAmount").GetInt64();
    }

    // How long before the stop a payment may be made and still wait out the outage: recovery takes it up once it is
    // StaleAfter old, in a pass every Interval (src/Payments.Api/Recovery/RecoveryOptions.cs). From the settings: the
    // manifest sets neither.
    private static double RecoveryReach()
    {
        using var json = JsonDocument.Parse(File.ReadAllText(Files[0]),
            new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        var recovery = json.RootElement.GetProperty("Recovery");
        TimeSpan Setting(string name) => TimeSpan.Parse(recovery.GetProperty(name).GetString()!, CultureInfo.InvariantCulture);
        return (Setting("StaleAfter") + Setting("Interval")).TotalSeconds;
    }

    public static string Roubles(long kopecks) => $"{(kopecks / 100m).ToString("0.##", CultureInfo.InvariantCulture)} ₽";

    /// <summary>Seconds from the stop, as the timeline of docs/observability.md has them: +345 с, −60 с.</summary>
    public static string At(double seconds) => seconds switch
    {
        > 0.5 => $"+{seconds:0} с",
        < -0.5 => $"−{-seconds:0} с",
        _ => "0 с",
    };

    private static string S(double seconds) => $"{seconds:0} с";

    [GeneratedRegex(@"name:\s*Antifraud__FailOpenBelowAmount\s+value:\s*""?(?<value>\d+)")]
    private static partial Regex FailOpenVariable();
}
