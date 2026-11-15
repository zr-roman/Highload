using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Failover;

/// <summary>What k6 measured, from the file of --summary-export.</summary>
public sealed record K6Result(
    long Requests, long Failed, long Succeeded, long Dropped, double Median, double P95, double P99, double Max,
    IReadOnlyList<string> CrossedThresholds)
{
    public static K6Result Read(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var metrics = document.RootElement.GetProperty("metrics");
        double Get(string metric, string stat) =>
            metrics.TryGetProperty(metric, out var values) && values.TryGetProperty(stat, out var value) ? value.GetDouble() : double.NaN;
        long Count(string metric, string stat) => double.IsNaN(Get(metric, stat)) ? 0 : (long)Get(metric, stat);

        var crossed = new List<string>();
        foreach (var metric in metrics.EnumerateObject())
            if (metric.Value.TryGetProperty("thresholds", out var thresholds))
                foreach (var threshold in thresholds.EnumerateObject())
                    if (threshold.Value.GetBoolean()) // true: crossed
                        crossed.Add($"{metric.Name} {threshold.Name}");

        return new K6Result(
            Requests: Count("http_reqs", "count"),
            // http_req_failed is a rate: its "passes" are the requests for which it is true, that is the failed ones.
            Failed: Count("http_req_failed", "passes"),
            // The one check of payments.js: "payment created".
            Succeeded: Count("checks", "passes"),
            Dropped: Count("dropped_iterations", "count"),
            Median: Get("http_req_duration", "med"),
            P95: Get("http_req_duration", "p(95)"),
            P99: Get("http_req_duration", "p(99)"),
            Max: Get("http_req_duration", "max"),
            CrossedThresholds: crossed);
    }
}

/// <summary>load/payments.js through Traefik, as load/node-failure.ps1 runs it.</summary>
public static class K6
{
    public static async Task<K6Result> RunAsync(FailoverOptions options, string reports, CancellationToken ct)
    {
        var summary = Path.Combine(reports, "k6.json");
        using var k6 = new LongProcess("k6",
        [
            "run", "--summary-export", summary,
            "-e", "BASE_URL=http://localhost:8080",
            "-e", $"RATE={options.Rate}",
            "-e", $"DURATION={options.LoadSeconds}s",
            "load/payments.js",
        ], Path.Combine(reports, "k6.txt"));
        await k6.WaitForExitAsync(ct);

        // 99: a threshold was crossed, which a failure run is expected to do. The summary is there all the same.
        if (k6.ExitCode is not (0 or 99) || !File.Exists(summary))
            throw new FailoverException($"k6 failed with exit code {k6.ExitCode}, see {Path.Combine(reports, "k6.txt")}");
        return K6Result.Read(summary);
    }
}

/// <summary>
/// Traefik's counters of attempts to Payments.Api by status code, through a port-forward for the length of the run.
/// They count attempts, not requests: an attempt that failed and was retried on another pod is counted too.
/// </summary>
public sealed partial class Traefik : IDisposable
{
    private readonly LongProcess portForward;
    private readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(10) };

    public Traefik(string reports) =>
        portForward = new LongProcess("kubectl", ["-n", "kube-system", "port-forward", "deploy/traefik", "9100:9100"],
            Path.Combine(reports, "traefik-port-forward.txt"));

    public async Task<Dictionary<string, double>?> CountsAsync()
    {
        try
        {
            var text = await http.GetStringAsync("http://localhost:9100/metrics");
            var counts = new Dictionary<string, double>();
            foreach (Match line in Counter().Matches(text))
                counts[line.Groups["code"].Value] = counts.GetValueOrDefault(line.Groups["code"].Value)
                    + double.Parse(line.Groups["value"].Value, CultureInfo.InvariantCulture);
            return counts;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            // If Traefik itself was on the node, its pod is gone and the port-forward with it.
            return null;
        }
    }

    public void Dispose()
    {
        http.Dispose();
        portForward.Dispose();
    }

    [GeneratedRegex(@"^traefik_service_requests_total\{code=""(?<code>\d+)"",[^}]*service=""highload-payments-api[^""]*""\}\s+(?<value>\S+)", RegexOptions.Multiline)]
    private static partial Regex Counter();
}
