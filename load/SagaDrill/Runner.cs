namespace SagaDrill;

public static class Runner
{
    public static async Task RunAsync(DrillOptions options, CancellationToken ct)
    {
        foreach (var file in new[] { "load/payments.js", Dashboard.File, Alerts.File })
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

        var path = Path.Combine(reports, "report.md");
        File.WriteAllText(path, Report.Markdown(options, prometheus.Route, preflight,
            commit.Ok ? commit.Output : null, dirty.Ok && dirty.Output.Length > 0, started));

        Console.WriteLine();
        foreach (var query in preflight.Queries.Where(q => !q.Ok))
        {
            Console.WriteLine($"no series  {query.What}: {query.Expression}");
            Console.WriteLine($"           {query.Result.Error ?? query.Found}");
        }
        foreach (var check in preflight.Checks)
            Console.WriteLine($"{(check.Ok ? "ok  " : "FAIL")}  {check.Name}: {check.Detail}");
        Console.WriteLine($"Report: {path}");
        if (preflight.Checks.Any(c => !c.Ok))
            throw new DrillException("Some checks failed, see the report");
    }
}
