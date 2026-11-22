using System.Text.Json;

namespace SagaDrill;

/// <summary>What k6 measured, from the file of --summary-export. The same as in load/Failover.</summary>
public sealed record K6Result(long Requests, long Failed, long Dropped, double Median, double P99)
{
    public static K6Result Read(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var metrics = document.RootElement.GetProperty("metrics");
        double Get(string metric, string stat) =>
            metrics.TryGetProperty(metric, out var values) && values.TryGetProperty(stat, out var value) ? value.GetDouble() : double.NaN;
        long Count(string metric, string stat) => double.IsNaN(Get(metric, stat)) ? 0 : (long)Get(metric, stat);

        return new K6Result(
            Requests: Count("http_reqs", "count"),
            // http_req_failed is a rate: its "passes" are the requests for which it is true, that is the failed ones.
            Failed: Count("http_req_failed", "passes"),
            Dropped: Count("dropped_iterations", "count"),
            Median: Get("http_req_duration", "med"),
            P99: Get("http_req_duration", "p(99)"));
    }
}

/// <summary>load/payments.js through Traefik, as load/Failover runs it.</summary>
public static class K6
{
    /// <param name="name">The stage of the drill: the files are &lt;name&gt;-k6.json and &lt;name&gt;-k6.txt.</param>
    public static async Task<K6Result> RunAsync(int rate, int seconds, string reports, string name, CancellationToken ct)
    {
        var summary = Path.Combine(reports, $"{name}-k6.json");
        var output = Path.Combine(reports, $"{name}-k6.txt");
        using var k6 = new LongProcess("k6",
        [
            "run", "--summary-export", summary,
            "-e", "BASE_URL=http://localhost:8080",
            "-e", $"RATE={rate}",
            "-e", $"DURATION={seconds}s",
            "load/payments.js",
        ], output);
        await k6.WaitForExitAsync(ct);

        // 99: a threshold was crossed. Not a reason to stop: the summary is there all the same, and the report has it.
        if (k6.ExitCode is not (0 or 99) || !File.Exists(summary))
            throw new DrillException($"k6 failed with exit code {k6.ExitCode}, see {output}");
        return K6Result.Read(summary);
    }
}
