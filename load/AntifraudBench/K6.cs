using System.Text.Json;
using System.Text.RegularExpressions;

namespace AntifraudBench;

/// <summary>
/// k6 in a container on the same network as the service: load/antifraud.js, mounted read-only, sends to the service by
/// its container name, so no request goes through Windows. Its results go to the folder of the bench through a second
/// mount. Started detached, so the sampler can watch the processor of k6 itself while it runs.
/// </summary>
public sealed partial class K6(string image, string repository, Stand stand, string reports)
{
    public const string Container = "highload-antifraud-bench-k6";

    public string Image { get; } = image;

    /// <summary>The image of the version of the k6 installed on Windows, unless one is given.</summary>
    public static async Task<string> ResolveImageAsync(string? given, CancellationToken ct)
    {
        if (given is not null)
            return given;

        var version = await Shell.RunAsync("k6", ["version"], ct);
        var match = Version().Match(version.Output);
        return match.Success
            ? $"grafana/k6:{match.Groups[1].Value}"
            : throw new BenchException($"Cannot tell the version of k6 from \"{version.Output}\": give the image with --k6-image");
    }

    public async Task PullAsync(CancellationToken ct)
    {
        Console.WriteLine($"Pulling {Image}");
        await Shell.RunCheckedAsync($"docker pull {Image}", "docker", ["pull", "-q", Image], ct);
    }

    /// <summary>Unmeasured load: no thresholds, no reports.</summary>
    public async Task WarmUpAsync(int rate, int seconds, CancellationToken ct)
    {
        var exitCode = await RunAsync(new Dictionary<string, string>(), ["--no-thresholds"], rate, seconds, Path.Combine(reports, "warmup.log"), sampler: null, ct);
        if (exitCode != 0)
            throw new BenchException($"The warm-up failed (k6 exit code {exitCode}), see {Path.Combine(reports, "warmup.log")}");
    }

    public async Task<K6Result> MeasureAsync(string label, int rate, int seconds, StatsSampler sampler, CancellationToken ct)
    {
        var log = Path.Combine(reports, $"{label}.log");
        Dictionary<string, string> environment = new()
        {
            // The charts of the step in an HTML file. Port -1: no live page, which would keep k6 from exiting.
            ["K6_WEB_DASHBOARD"] = "true",
            ["K6_WEB_DASHBOARD_PORT"] = "-1",
            ["K6_WEB_DASHBOARD_PERIOD"] = "2s",
            ["K6_WEB_DASHBOARD_EXPORT"] = $"/out/{label}.html",
        };

        var exitCode = await RunAsync(environment, ["--summary-export", $"/out/{label}.json"], rate, seconds, log, sampler, ct);
        // 99: some thresholds were crossed. That is a result, not a failure of the run: the table says which.
        if (exitCode is not (0 or 99))
            throw new BenchException($"k6 failed in {label} with exit code {exitCode}, see {log}");

        return K6Result.Read(Path.Combine(reports, $"{label}.json"));
    }

    private async Task<int> RunAsync(
        IReadOnlyDictionary<string, string> environment, IEnumerable<string> options, int rate, int seconds,
        string logPath, StatsSampler? sampler, CancellationToken ct)
    {
        await RemoveAsync();

        List<string> arguments =
        [
            "run", "-d", "--name", Container, "--network", stand.Network,
            "--mount", $"type=bind,source={Path.Combine(repository, "load")},target=/scripts,readonly",
            "--mount", $"type=bind,source={reports},target=/out",
        ];
        foreach (var (name, value) in environment)
            arguments.AddRange(["-e", $"{name}={value}"]);
        arguments.AddRange(
        [
            Image, "run", .. options,
            "-e", $"BASE_URL=http://{Stand.Container}:{Stand.Port}",
            "-e", $"RATE={rate}",
            "-e", $"DURATION={seconds}s",
            "/scripts/antifraud.js",
        ]);

        try
        {
            await Shell.RunCheckedAsync("docker run of k6", "docker", arguments, ct);
            sampler?.Start(Container);

            var exit = await Shell.RunCheckedAsync("docker wait of k6", "docker", ["wait", Container], ct);
            var logs = await Shell.RunAsync("docker", ["logs", Container], CancellationToken.None);
            await File.WriteAllTextAsync(logPath, logs.Output + "\n" + logs.Errors, CancellationToken.None);
            return int.Parse(exit);
        }
        finally
        {
            sampler?.Stop();
            await RemoveAsync();
        }
    }

    public static async Task RemoveAsync() =>
        await Shell.RunAsync("docker", ["rm", "-f", Container], CancellationToken.None);

    [GeneratedRegex(@"v(\d+\.\d+\.\d+)")]
    private static partial Regex Version();
}

/// <summary>What one k6 step measured, from the file of --summary-export.</summary>
public sealed record K6Result(
    long Requests,
    double RequestsPerSecond,
    double P50,
    double P95,
    double P99,
    long Failed,
    long Status503,
    long Denied,
    long Dropped,
    double DroppedPerSecond,
    IReadOnlyList<string> CrossedThresholds)
{
    public double FailedShare => Requests > 0 ? (double)Failed / Requests : 0;

    public static K6Result Read(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var metrics = document.RootElement.GetProperty("metrics");

        // A tagged sub-metric is in the summary only if antifraud.js gives it a threshold, as it does for these.
        // With nothing of its kind, its numbers are zeros.
        double Get(string metric, string stat) =>
            metrics.TryGetProperty(metric, out var values) && values.TryGetProperty(stat, out var value) ? value.GetDouble() : 0;

        var crossed = new List<string>();
        foreach (var metric in metrics.EnumerateObject())
            if (metric.Value.TryGetProperty("thresholds", out var thresholds))
                foreach (var threshold in thresholds.EnumerateObject())
                    if (threshold.Value.GetBoolean()) // true: crossed
                        crossed.Add($"{metric.Name} {threshold.Name}");

        return new K6Result(
            Requests: (long)Get("http_reqs", "count"),
            RequestsPerSecond: Get("http_reqs", "rate"),
            P50: Get("http_req_duration", "med"),
            P95: Get("http_req_duration", "p(95)"),
            P99: Get("http_req_duration", "p(99)"),
            // http_req_failed is a rate: its "passes" are the requests for which it is true, that is the failed ones.
            Failed: (long)Get("http_req_failed", "passes"),
            Status503: (long)Get("http_reqs{status:503}", "count"),
            Denied: (long)Get("decisions{decision:deny}", "count"),
            Dropped: (long)Get("dropped_iterations", "count"),
            DroppedPerSecond: Get("dropped_iterations", "rate"),
            CrossedThresholds: crossed);
    }
}
