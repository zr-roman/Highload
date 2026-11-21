namespace AntifraudBench;

public static class BenchRunner
{
    public static async Task RunAsync(BenchOptions options, CancellationToken ct)
    {
        var repository = FindRepository();
        var started = DateTimeOffset.Now;
        var reports = Path.Combine(repository, "load", "reports", $"antifraud-bench-{started:yyyyMMdd-HHmmss}");
        Directory.CreateDirectory(reports);

        var stand = await Stand.PrepareAsync(repository, ct);
        var k6Image = await K6.ResolveImageAsync(options.K6Image, ct);
        if (options.Build)
            await stand.BuildAsync(Path.Combine(reports, "docker-build.log"), ct);
        else
            await stand.CheckImageAsync(ct);

        var k6 = new K6(k6Image, repository, stand, reports);
        await k6.PullAsync(ct);

        var report = new Report(reports, options, k6Image, await CommitAsync(repository, ct), started);
        var sampler = new StatsSampler();
        var steps = new List<StepResult>();

        Console.WriteLine($"Network {stand.Network}, k6 {k6Image}, reports in {reports}");
        try
        {
            foreach (var workers in options.Workers)
            {
                Console.WriteLine($"Antifraud with {workers} uvicorn process(es)");
                await stand.StartAsync(workers, options.RedisTimeoutMs, options.Cpus, ct);
                await stand.WaitReadyAsync(workers, ct);

                if (options.WarmupSeconds > 0)
                {
                    Console.WriteLine($"  warm-up: {options.Rates[0]}/s for {options.WarmupSeconds} s, not measured");
                    await stand.FlushRedisAsync(ct);
                    await k6.WarmUpAsync(options.Rates[0], options.WarmupSeconds, ct);
                }

                foreach (var rate in options.Rates)
                {
                    for (var repeat = 1; repeat <= options.Repeats; repeat++)
                    {
                        await stand.FlushRedisAsync(ct);
                        var result = await k6.MeasureAsync($"w{workers}-r{rate}-{repeat}", rate, options.DurationSeconds, sampler, ct);
                        var step = new StepResult(
                            workers, rate, repeat, result,
                            sampler.Of(Stand.Container), sampler.Of(Stand.RedisContainer), sampler.Of(K6.Container),
                            await stand.ReadRedisAsync(ct));

                        steps.Add(step);
                        report.Write(steps);
                        Console.WriteLine(step.ConsoleLine);
                    }
                }

                await stand.SaveLogsAsync(Path.Combine(reports, $"antifraud-w{workers}.log"));
            }
        }
        finally
        {
            sampler.Stop();
            await K6.RemoveAsync();
            await stand.SaveLogsAsync(Path.Combine(reports, "antifraud-last.log"));
            await stand.RemoveAsync();
            report.Write(steps);
            Console.WriteLine($"Table: {report.Path}");
        }
    }

    // The bench reads load/antifraud.js and builds the image from the repository root, wherever it is started from.
    private static string FindRepository()
    {
        for (var folder = new DirectoryInfo(Directory.GetCurrentDirectory()); folder is not null; folder = folder.Parent)
        {
            if (File.Exists(Path.Combine(folder.FullName, "payments-platform.slnx")))
                return folder.FullName;
        }
        throw new BenchException("Run the bench inside the repository: payments-platform.slnx is not found above the current folder");
    }

    private static async Task<string> CommitAsync(string repository, CancellationToken ct)
    {
        var head = await Shell.RunAsync("git", ["rev-parse", "--short", "HEAD"], ct, repository);
        var changes = await Shell.RunAsync("git", ["status", "--porcelain"], ct, repository);
        return head.ExitCode != 0
            ? "неизвестен"
            : $"`{head.Output}`{(changes.Output.Length > 0 ? " и незакоммиченные правки" : "")}";
    }
}
