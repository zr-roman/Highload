using System.Diagnostics;
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
        if (options.HotQuota > 0)
            settings = settings with { HotTokensPerSecond = options.HotQuota, HotBurst = options.HotBurst };
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

        // A repeat takes its load and about 40 s more: k6 starting and stopping, the catch-up and /invariants, which reads
        // the whole ledger: 20-60 s with millions of payments in it.
        var minutes = (repeats * (options.DurationSeconds + 40) + groups.Count * (options.WarmupSeconds + 30)) / 60.0;
        Console.WriteLine($"{repeats} repeat{(repeats == 1 ? "" : "s")} of {options.DurationSeconds} s, about {Math.Ceiling(minutes)} min. Report: {reports}");

        var ignored = Environment.GetEnvironmentVariables().Keys.Cast<string>().Where(Processes.IsStandSetting).Order().ToList();
        if (ignored.Count > 0)
            Console.WriteLine($"Not passed on to the stand from this shell: {string.Join(", ", ignored)}");

        await using var payments = NpgsqlDataSource.Create(settings.PaymentsDatabase);

        Console.WriteLine("Checking the stand");
        StandInfo info;
        using (var checks = KafkaClient(settings))
        {
            info = await Preflight.CheckAsync(
                repository, settings, payments, checks, Stand.PaymentsApiUrlsOf(options.Instances), Stand.LedgerUrlsOf(options.Ledgers), ct);

            // After the checks: the services are not running, so nothing holds the tables, the topic or the group.
            if (options.Fresh)
            {
                Console.WriteLine("Emptying the stand (--fresh)");
                await FreshStart.EmptyAsync(settings, checks, ct);
            }
        }

        // A client of its own for the samples: the one that saw the topic deleted keeps that in its metadata,
        // and for the topic the services create anew it found "No leaders" sample after sample.
        using var kafka = KafkaClient(settings);

        using var stand = new Stand(repository, reports, options);
        if (options.RedisPauseSeconds > 0)
            await stand.EnsureRedisCanPauseAsync(ct);

        Console.WriteLine("Building the services in Release");
        await stand.BuildAsync(ct);

        var backlog = new Backlog(payments, kafka, settings.LedgerGroup, stand.EnsureRunning);
        var table = new PaymentsTable(payments);
        var k6 = new K6(Path.Combine(repository, "load", "payments.js"), reports, stand.PaymentsApiUrls);
        // /invariants reads every posting of the ledger, which takes a while on a big one.
        using var ledger = new HttpClient { BaseAddress = new Uri(Stand.LedgerUrl), Timeout = TimeSpan.FromMinutes(3) };
        var report = new Report(Path.Combine(reports, "table.md"), info, settings, options);
        var results = new List<RunResult>();

        foreach (var group in groups)
        {
            Console.WriteLine($"{(group == groups[0] ? "Starting the stand" : "Restarting Payments.Api")}, limiter {(group.Key ? "on" : "off")}" +
                (options.Instances > 1 ? $", {options.Instances} instances of Payments.Api" : "") +
                (options.Ledgers > 1 ? $", {options.Ledgers} instances of Ledger" : ""));
            await stand.StartAsync(limiter: group.Key, ct);
            await backlog.WaitLedgerAssignedAsync(options.Ledgers, ct);

            // After the start: Payments.Api has applied schema.sql, so the table is there.
            if (group == groups[0] && (options.PrefillRows > 0 || options.StaleIndex is not null))
            {
                Console.WriteLine("Preparing the table payments");
                if (options.PrefillRows > 0)
                    await table.PrefillAsync(options.PrefillRows, ct);
                if (options.StaleIndex is { } staleIndex)
                    await table.SetStaleIndexAsync(staleIndex, ct);
            }

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
            // Every run starts from a stand that has caught up with the load before it: the warm-up or the run before.
            Console.WriteLine($"Waiting for the stand to catch up before run {label}");
            var start = await backlog.WaitCaughtUpAsync(ct);
            var ledgerMembers = await backlog.ReadLedgerAssignmentAsync(ct);
            var updatesBefore = await table.ReadUpdatesAsync(ct);

            Console.WriteLine($"Run {label}: {run.Rate}/s, hot share {run.HotShare}, limiter {(run.Limiter ? "on" : "off")}, {options.DurationSeconds} s");

            using var stopLoad = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var load = k6.RunAsync(run, label, options.DurationSeconds, stopLoad.Token);

            // --redis-pause: in the middle of the load, so that there is load before the outage and after it.
            var pause = options.RedisPauseSeconds > 0
                ? stand.PauseRedisAsync(
                    after: TimeSpan.FromSeconds((options.DurationSeconds - options.RedisPauseSeconds) / 2.0),
                    duration: TimeSpan.FromSeconds(options.RedisPauseSeconds),
                    stopLoad.Token)
                : Task.CompletedTask;

            IReadOnlyList<BacklogSample> samples;
            try
            {
                samples = await backlog.RecordAsync(load, Path.Combine(reports, $"run-{label}-backlog.csv"), start, ct);
            }
            catch
            {
                // A process of the stand died, or the stand never caught up: k6 is killed, and waited for, so that it
                // does not go on loading a broken stand after the matrix has exited.
                stopLoad.Cancel();
                try
                {
                    await Task.WhenAll(load, pause);
                }
                catch
                {
                    // How k6 ended is not news: the error that stopped the run is.
                }
                throw;
            }
            var k6Result = await load;
            await pause;
            var check = await CheckLedgerAsync(label);
            var updatesAfter = await table.ReadUpdatesAsync(ct);
            var staleSearch = await table.ExplainStaleSearchAsync(ct);

            var result = new RunResult(
                run,
                label,
                options.DurationSeconds,
                k6Result,
                BacklogSummary.Of(samples, k6Result.Finished),
                LimiterOutages: stand.RedisTimeouts(k6Result.Started, k6Result.Finished),
                Ledger: check,
                LedgerMembers: ledgerMembers,
                HotShare: UpdateCounts.HotShare(updatesBefore, updatesAfter),
                StaleSearch: staleSearch);
            Console.WriteLine(Describe(result, settings));
            return result;
        }

        // The stand has caught up: every payment captured during the run is in the ledger by now. A check that fails
        // leaves its repeat without it, not the matrix without the repeats after it.
        async Task<LedgerCheck?> CheckLedgerAsync(string label)
        {
            var took = Stopwatch.StartNew();
            try
            {
                var invariants = await ledger.GetStringAsync("/invariants", ct);
                await File.WriteAllTextAsync(Path.Combine(reports, $"run-{label}-invariants.json"), invariants, ct);
                return LedgerCheck.Read(invariants, took.Elapsed);
            }
            catch (Exception e) when (e is HttpRequestException || (e is TaskCanceledException && !ct.IsCancellationRequested))
            {
                Console.WriteLine($"  /invariants of the ledger failed after {took.Elapsed.TotalSeconds:0} s: {e.Message}");
                return null;
            }
        }
    }

    private static string Describe(RunResult result, StandSettings settings)
    {
        var line = $"  p99 other {result.K6.P99Other:0} ms, 202 other {result.K6.Other202Share:0.#%}";
        if (result.Run.HasHotMerchant)
        {
            line += $", hot 201 {result.K6.P99Hot201:0} ms, 202 hot {result.K6.Hot202Share:0.#%}; hot accepted {(double)result.HotAccepted / result.DurationSeconds:0}/s";
            if (result.Run.Limiter)
                line += $", over the limit {result.OverLimit(settings)}, Redis failures {result.LimiterOutages}";
        }

        line += $"; ledger wrote at most {result.Backlog.PeakWriteTotal:0}/s";
        line += $"; HOT updates {(result.HotShare is { } hot ? $"{hot:0.#%}" : "none")}, stale search {result.StaleSearch.Milliseconds:0} ms ({result.StaleSearch.Scan})";
        if (result.HotPartitionPeakWrite is { } hotWrite)
            line += $", {hotWrite:0}/s to the hot partition";

        return line +
            $"; dropped {result.K6.Dropped}; outbox max {result.Backlog.OutboxMaxAgeSeconds:0.0} s; lag max {result.Backlog.LagMax.Max()}" +
            $"; caught up in {result.Backlog.CaughtUpSeconds:0} s; samples at most {result.Backlog.LongestGapSeconds:0} s apart" +
            $"; thresholds {(result.K6.CrossedThresholds.Count == 0 ? "ok" : "CROSSED")}" +
            (result.Ledger is { } check ? $"; invariants {(check.Ok ? "ok" : "BROKEN")} in {check.Took.TotalSeconds:0} s" : "; invariants not checked");
    }

    private static IAdminClient KafkaClient(StandSettings settings) =>
        new AdminClientBuilder(new AdminClientConfig { BootstrapServers = settings.Kafka })
            .SetLogHandler((_, _) => { }) // librdkafka's own log is noise here: a call that fails throws anyway
            .Build();

    // dotnet run starts the matrix from load/Matrix/bin/<configuration>/net10.0; the repository is above, with the solution.
    private static string FindRepository()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "payments-platform.slnx")))
                return directory.FullName;

        throw new MatrixException($"payments-platform.slnx not found above {AppContext.BaseDirectory}");
    }
}
