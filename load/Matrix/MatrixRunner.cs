using System.Text.Json;
using Confluent.Kafka;
using Npgsql;

namespace Matrix;

/// <summary>The matrix from start to end: checks, the build, the stand, then the repeats one by one, with the table after each.</summary>
public static class MatrixRunner
{
    public static async Task RunAsync(MatrixOptions options, CancellationToken ct)
    {
        var repository = FindRepository();
        var settings = StandSettings.Read(repository);
        var reports = Path.Combine(repository, "load", "reports", $"matrix-{DateTime.Now:yyyyMMdd-HHmmss}");
        Directory.CreateDirectory(reports);

        // The runs with the limiter first, then Payments.Api restarts without it. In each group the repeats go round
        // the runs (1a 2a ... 1b 2b ...): a slow minute of the laptop then spreads over the runs instead of hitting one.
        var groups = MatrixRun.All
            .Where(run => options.Runs.Contains(run.Id))
            .GroupBy(run => run.Limiter)
            .OrderByDescending(group => group.Key)
            .ToList();
        var repeats = groups.Sum(group => group.Count()) * options.Repeats;
        // A repeat takes its load and about 15 s more: k6 starting and stopping, the catch-up, /invariants.
        var minutes = (repeats * (options.DurationSeconds + 15) + groups.Count * (options.WarmupSeconds + 30)) / 60.0;
        Console.WriteLine($"{repeats} repeat{(repeats == 1 ? "" : "s")} of {options.DurationSeconds} s, about {Math.Ceiling(minutes)} min. Report: {reports}");

        var ignored = Environment.GetEnvironmentVariables().Keys.Cast<string>().Where(Processes.IsStandSetting).Order().ToList();
        if (ignored.Count > 0)
            Console.WriteLine($"Not passed on to the stand from this shell: {string.Join(", ", ignored)}");

        await using var payments = NpgsqlDataSource.Create(settings.PaymentsDatabase);
        using var kafka = new AdminClientBuilder(new AdminClientConfig { BootstrapServers = settings.Kafka })
            .SetLogHandler((_, _) => { }) // librdkafka's own log is noise here: a call that fails throws anyway
            .Build();

        Console.WriteLine("Checking the stand");
        var info = await Preflight.CheckAsync(repository, settings, payments, kafka, ct);

        using var stand = new Stand(repository, reports);
        Console.WriteLine("Building the services in Release");
        await stand.BuildAsync(ct);

        var backlog = new Backlog(payments, kafka, settings.LedgerGroup, stand.EnsureRunning);
        var k6 = new K6(Path.Combine(repository, "load", "payments.js"), reports);
        // /invariants reads every posting of the ledger, which takes a while on a big one.
        using var ledger = new HttpClient { BaseAddress = new Uri(Stand.LedgerUrl), Timeout = TimeSpan.FromMinutes(3) };
        var report = new Report(Path.Combine(reports, "table.md"), info, settings, options);
        var results = new List<RunResult>();

        foreach (var group in groups)
        {
            Console.WriteLine($"{(group == groups[0] ? "Starting the stand" : "Restarting Payments.Api")}, limiter {(group.Key ? "on" : "off")}");
            await stand.StartAsync(limiter: group.Key, ct);

            if (options.WarmupSeconds > 0)
            {
                Console.WriteLine($"Warm-up, {options.WarmupSeconds} s");
                await k6.WarmUpAsync(group.First(), options.WarmupSeconds, ct);
            }

            for (var repeat = 0; repeat < options.Repeats; repeat++)
                foreach (var run in group)
                {
                    results.Add(await MeasureAsync(run, label: $"{run.Id}{(char)('a' + repeat)}"));
                    report.Write(results);
                }
        }

        Console.WriteLine($"Done: {report.Path}");

        async Task<RunResult> MeasureAsync(MatrixRun run, string label)
        {
            Console.WriteLine($"Run {label}: {run.Rate}/s, hot share {run.HotShare}, limiter {(run.Limiter ? "on" : "off")}, {options.DurationSeconds} s");

            // Every run starts from a stand that has caught up with the load before it.
            await backlog.WaitCaughtUpAsync(ct);

            using var stopLoad = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var load = k6.RunAsync(run, label, options.DurationSeconds, stopLoad.Token);
            IReadOnlyList<BacklogSample> samples;
            try
            {
                samples = await backlog.RecordAsync(load, Path.Combine(reports, $"run-{label}-backlog.csv"), ct);
            }
            catch
            {
                // A process of the stand died, or the stand never caught up: k6 is killed, and waited for, so that it
                // does not go on loading a broken stand after the matrix has exited.
                stopLoad.Cancel();
                try
                {
                    await load;
                }
                catch
                {
                    // How k6 ended is not news: the error that stopped the run is.
                }
                throw;
            }
            var k6Result = await load;

            // The stand has caught up: every payment captured during the run is in the ledger by now.
            string invariants;
            try
            {
                invariants = await ledger.GetStringAsync("/invariants", ct);
            }
            catch (Exception e) when (e is HttpRequestException || (e is TaskCanceledException && !ct.IsCancellationRequested))
            {
                throw new MatrixException($"/invariants of the ledger failed after run {label}: {e.Message}");
            }
            await File.WriteAllTextAsync(Path.Combine(reports, $"run-{label}-invariants.json"), invariants, ct);
            using var answer = JsonDocument.Parse(invariants);

            var result = new RunResult(
                run,
                label,
                options.DurationSeconds,
                k6Result,
                BacklogSummary.Of(samples, k6Result.Finished),
                LimiterOutages: stand.RedisTimeouts(k6Result.Started, k6Result.Finished),
                InvariantsOk: answer.RootElement.GetProperty("ok").GetBoolean());
            Console.WriteLine(Describe(result, settings));
            return result;
        }
    }

    private static string Describe(RunResult result, StandSettings settings)
    {
        var line = $"  p99 other {result.K6.P99Other:0} ms";
        if (result.Run.HasHotMerchant)
        {
            line += $", hot 201 {result.K6.P99Hot201:0} ms; hot accepted {(double)result.HotAccepted / result.DurationSeconds:0}/s";
            if (result.Run.Limiter)
                line += $", over the limit {result.OverLimit(settings)}, limiter outages {result.LimiterOutages}";
        }

        return line +
            $"; dropped {result.K6.Dropped}; outbox max {result.Backlog.OutboxMaxAgeSeconds:0.0} s; lag max {result.Backlog.LagMax.Max()}" +
            $"; caught up in {result.Backlog.CaughtUpSeconds:0} s; thresholds {(result.K6.CrossedThresholds.Count == 0 ? "ok" : "CROSSED")}" +
            $"; invariants {(result.InvariantsOk ? "ok" : "BROKEN")}";
    }

    // dotnet run starts the matrix from load/Matrix/bin/<configuration>/net10.0; the repository is above, with the solution.
    private static string FindRepository()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "payments-platform.slnx")))
                return directory.FullName;

        throw new MatrixException($"payments-platform.slnx not found above {AppContext.BaseDirectory}");
    }
}
