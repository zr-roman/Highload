namespace SagaDrill;

/// <summary>A phase as the run left it: what it found, or why it stopped, or why it was not run.</summary>
/// <param name="Stopped">The phase could not go on (k6 failed, the stand never settled): a failed check, not a report lost.</param>
public sealed record PhaseRun(string Title, IPhaseResult? Result, Check? Stopped, string? NotRun)
{
    public IReadOnlyList<Check> Checks => Result?.Checks ?? (Stopped is null ? [] : [Stopped]);

    public bool Ok => NotRun is null && Checks.Count > 0 && Checks.All(c => c.Ok);
}

public static class Runner
{
    public static async Task RunAsync(DrillOptions options, CancellationToken ct)
    {
        string[] files = ["load/payments.js", Dashboard.File, Alerts.File, Preflight.SmokeScript, .. OutagePhase.Files];
        foreach (var file in files)
            if (!File.Exists(file))
                throw new DrillException($"Run it from the root of the repository: {file} is not here");

        var reports = Path.Combine("load", "reports", $"saga-drill-{DateTime.Now:yyyyMMdd-HHmmss}");
        Directory.CreateDirectory(reports);
        var commit = await Shell.RunAsync("git", ["rev-parse", "--short=12", "HEAD"]);
        var dirty = await Shell.RunAsync("git", ["status", "--porcelain", "--untracked-files=no"]);
        var started = DateTimeOffset.Now;

        Console.WriteLine($"Preflight: the stand, the rules of {Alerts.Group}, the queries of the saga's panels and alerts");
        using var prometheus = await Prometheus.OpenAsync(options.PrometheusUrl, reports, ct);
        var preflight = await Preflight.RunAsync(options, prometheus, reports, ct);

        // A phase on a stand that failed its preflight, or the phase before, would measure the stand, not the saga.
        var phases = new List<PhaseRun>();
        var phase = new (Part Part, string Title, Func<Task<IPhaseResult>> Run)[]
        {
            (Part.Deny, "Фаза «отказы»", async () => await DenyPhase.RunAsync(options, prometheus, reports, ct)),
            (Part.Outage, "Фаза «антифрод лежит»", async () => await OutagePhase.RunAsync(options, prometheus, reports, ct)),
            (Part.Redis, "Фаза «Redis лежит»", async () => await RedisPhase.RunAsync(options, prometheus, reports, ct)),
            (Part.Kill, "Фаза «всё отклоняется, Payments.Api убит»", async () => await KillPhase.RunAsync(options, prometheus, reports, ct)),
        };
        string? notRun = preflight.Checks.All(c => c.Ok) ? null : "предпроверка не прошла";
        foreach (var (part, title, run) in phase.Where(p => options.Runs(p.Part)))
        {
            if (notRun is not null)
            {
                phases.Add(new PhaseRun(title, null, null, notRun));
                continue;
            }
            try
            {
                phases.Add(new PhaseRun(title, await run(), null, null));
            }
            catch (DrillException e)
            {
                phases.Add(new PhaseRun(title, null, new Check($"{title.ToLowerInvariant()} дошла до конца", false, e.Message), null));
            }
            if (!phases[^1].Ok)
                notRun = $"{title.ToLowerInvariant()} не прошла";
        }
        if (phases.Any(p => p.NotRun is not null))
            Console.WriteLine($"Not run: {string.Join(", ", phases.Where(p => p.NotRun is not null).Select(p => p.Title))}");

        var path = Path.Combine(reports, "report.md");
        File.WriteAllText(path, Report.Markdown(options, prometheus.Route, preflight, phases,
            commit.Ok ? commit.Output : null, dirty.Ok && dirty.Output.Length > 0, started));

        Console.WriteLine();
        foreach (var query in preflight.Queries.Where(q => !q.Ok))
        {
            Console.WriteLine($"no series  {query.What}: {query.Expression}");
            Console.WriteLine($"           {query.Result.Error ?? query.Found}");
        }
        var checks = preflight.Checks.Concat(phases.SelectMany(p => p.Checks)).ToList();
        foreach (var check in checks)
            Console.WriteLine($"{(check.Ok ? "ok  " : "FAIL")}  {check.Name}: {check.Detail}");
        Console.WriteLine($"Report: {path}");
        if (checks.Any(c => !c.Ok))
            throw new DrillException("Some checks failed, see the report");
    }
}
