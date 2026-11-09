using System.Diagnostics;
using System.Text.Json;

namespace Matrix;

/// <summary>
/// Runs load/payments.js with the merchants of stage 3 and reads what k6 measured. With several instances of
/// Payments.Api the requests go round them in turn.
/// </summary>
public sealed class K6(string script, string reports, IReadOnlyList<string> paymentsApiUrls)
{
    // Stage 3: merchant 1 is the hot one, the rest of the payments go to merchants 2 to 50 equally.
    public const int HotMerchant = 1;
    public const int Merchants = 50;

    /// <summary>Unmeasured load after a start of Payments.Api, so that the first run does not pay for JIT and cold pools.</summary>
    public async Task WarmUpAsync(MatrixRun run, int seconds, CancellationToken ct)
    {
        // Not above 1000/s, below the knee of the stand: a warm-up past it would leave a backlog for the first run.
        var command = Command(run with { Rate = Math.Min(run.Rate, 1000) }, seconds, "--no-thresholds");
        command.Environment["K6_WEB_DASHBOARD"] = "false";

        var log = Path.Combine(reports, $"warmup-limiter-{(run.Limiter ? "on" : "off")}.txt");
        if (await Processes.RunAsync(command, log, ct) != 0)
            throw new MatrixException($"The warm-up failed, see {log}");
    }

    public async Task<K6Result> RunAsync(MatrixRun run, string label, int seconds, CancellationToken ct)
    {
        var summary = Path.Combine(reports, $"run-{label}.json");
        var command = Command(run, seconds, "--summary-export", summary);
        // The charts of the run in an HTML file, as ladder.ps1 makes them. Port -1: no live page, which would keep k6
        // from exiting while a browser tab has it open.
        command.Environment["K6_WEB_DASHBOARD"] = "true";
        command.Environment["K6_WEB_DASHBOARD_PORT"] = "-1";
        command.Environment["K6_WEB_DASHBOARD_PERIOD"] = "2s";
        command.Environment["K6_WEB_DASHBOARD_EXPORT"] = Path.Combine(reports, $"run-{label}.html");

        var log = Path.Combine(reports, $"run-{label}.txt");
        var started = DateTimeOffset.Now;
        var exitCode = await Processes.RunAsync(command, log, ct);
        var finished = DateTimeOffset.Now;

        // 105: k6 was stopped from outside, as by a Ctrl+C, which reaches k6 as well as the matrix.
        if (exitCode == 105)
            throw new OperationCanceledException($"k6 was stopped in run {label}");

        // 99: a threshold was crossed. That is a result of the run, and the summary is there all the same.
        if (exitCode is not (0 or 99) || !File.Exists(summary))
            throw new MatrixException($"k6 failed in run {label} with exit code {exitCode}, see {log}");

        return K6Result.Read(summary, started, finished);
    }

    private ProcessStartInfo Command(MatrixRun run, int seconds, params string[] options) =>
        Processes.Info("k6",
        [
            "run", .. options,
            "-e", $"RATE={run.Rate}",
            "-e", $"DURATION={seconds}s",
            "-e", $"HOT_SHARE={run.HotShare}",
            "-e", $"HOT_MERCHANT={HotMerchant}",
            "-e", $"MERCHANTS={Merchants}",
            "-e", $"BASE_URL={string.Join(',', paymentsApiUrls)}",
            script,
        ]);
}

/// <summary>What one k6 run measured, from the file of --summary-export.</summary>
public sealed record K6Result(
    DateTimeOffset Started,
    DateTimeOffset Finished,
    double P99Other,
    double P99Hot,
    double P99Hot201,
    long HotRequests,
    long HotRateLimited,
    long HotFailed,
    long Dropped,
    IReadOnlyList<string> CrossedThresholds)
{
    public static K6Result Read(string path, DateTimeOffset started, DateTimeOffset finished)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var metrics = document.RootElement.GetProperty("metrics");

        // A tagged sub-metric is in the summary only if payments.js gives it a threshold, as it does for all of these
        // once HOT_SHARE is set. With no requests of its kind, its numbers are zeros.
        double Get(string metric, string stat) =>
            metrics.TryGetProperty(metric, out var values) && values.TryGetProperty(stat, out var value) ? value.GetDouble() : 0;

        var crossed = new List<string>();
        foreach (var metric in metrics.EnumerateObject())
            if (metric.Value.TryGetProperty("thresholds", out var thresholds))
                foreach (var threshold in thresholds.EnumerateObject())
                    if (threshold.Value.GetBoolean()) // true: crossed
                        crossed.Add($"{metric.Name} {threshold.Name}");

        return new K6Result(
            started,
            finished,
            P99Other: Get("http_req_duration{merchant:other}", "p(99)"),
            P99Hot: Get("http_req_duration{merchant:hot}", "p(99)"),
            P99Hot201: Get("http_req_duration{merchant:hot,status:201}", "p(99)"),
            HotRequests: (long)Get("http_reqs{merchant:hot}", "count"),
            HotRateLimited: (long)Get("rate_limited{merchant:hot}", "count"),
            // http_req_failed is a rate: its "passes" are the requests for which it is true, that is the failed ones.
            HotFailed: (long)Get("http_req_failed{merchant:hot}", "passes"),
            Dropped: (long)Get("dropped_iterations", "count"),
            CrossedThresholds: crossed);
    }
}
