using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace SagaDrill;

/// <summary>One series of an instant query: its labels and its value at the time of the query.</summary>
public sealed record Series(IReadOnlyDictionary<string, string> Labels, double Value);

/// <summary>What an instant query returned: the series, or why there are none to look at.</summary>
public sealed record QueryResult(IReadOnlyList<Series> Series, string? Error);

/// <summary>One series of a range selector: its labels and its raw samples, each at the time Prometheus stamped it.</summary>
public sealed record Samples(IReadOnlyDictionary<string, string> Labels, IReadOnlyList<(double Time, double Value)> Points);

/// <summary>What a range selector returned: the series, or why there are none to look at.</summary>
public sealed record SamplesResult(IReadOnlyList<Samples> Series, string? Error);

/// <summary>An alerting rule as Prometheus has loaded it, with its query in Prometheus' own formatting.</summary>
/// <param name="Duration">Its for, in seconds: how long the condition must hold before the alert fires.</param>
public sealed record LoadedRule(string Name, string Query, double Duration, string Health, string? LastError);

/// <summary>A group of rules as Prometheus has loaded it.</summary>
/// <param name="Interval">How often Prometheus evaluates the rules of the group, in seconds.</param>
public sealed record LoadedGroup(double Interval, IReadOnlyList<LoadedRule> Rules);

/// <summary>
/// The Prometheus of the stand over its HTTP API, through a port-forward for the length of the run. The local port
/// is a free one, not 9090: a port-forward of your own may hold that one, and two on one port would fight.
/// </summary>
public sealed class Prometheus : IDisposable
{
    private const string Service = "svc/monitoring-prometheus";

    private readonly HttpClient http;
    private readonly LongProcess? portForward;
    private readonly string? portForwardLog;

    private Prometheus(string address, LongProcess? portForward, string? portForwardLog)
    {
        Address = address;
        this.portForward = portForward;
        this.portForwardLog = portForwardLog;
        http = new HttpClient { BaseAddress = new Uri(address + "/"), Timeout = TimeSpan.FromSeconds(15) };
    }

    public string Address { get; }

    /// <summary>How the tool reaches Prometheus, for the report.</summary>
    public string Route => portForward is null ? Address : $"port-forward `{Service}` на {Address}";

    /// <summary>At the given address, or through a port-forward the tool opens and closes.</summary>
    public static async Task<Prometheus> OpenAsync(string? address, string reports, CancellationToken ct)
    {
        Prometheus prometheus;
        if (address is not null)
            prometheus = new Prometheus(address, null, null);
        else
        {
            var log = Path.Combine(reports, "prometheus-port-forward.txt");
            var port = FreePort();
            prometheus = new Prometheus($"http://127.0.0.1:{port}",
                new LongProcess("kubectl", ["-n", "monitoring", "port-forward", Service, $"{port}:9090"], log), log);
        }

        try
        {
            await prometheus.WaitReadyAsync(TimeSpan.FromSeconds(30), ct);
            return prometheus;
        }
        catch
        {
            prometheus.Dispose();
            throw;
        }
    }

    public async Task<QueryResult> QueryAsync(string expression, CancellationToken ct)
    {
        var (data, error) = await QueryDataAsync("query=" + Uri.EscapeDataString(expression), ct);

        if (data is not { } result)
            return new QueryResult([], error);

        if (result.GetProperty("resultType").GetString() == "scalar")
            return new QueryResult([new Series(new Dictionary<string, string>(), Value(result.GetProperty("result")[1]))], null);

        var series = new List<Series>();

        foreach (var item in result.GetProperty("result").EnumerateArray())
            series.Add(new Series(Labels(item), Value(item.GetProperty("value")[1])));

        return new QueryResult(series, null);
    }

    /// <summary>
    /// The raw samples of a selector from <paramref name="from"/> to <paramref name="to"/>, in seconds since 1970: what
    /// a rule wrote, each sample at the time of its evaluation, where a range query would give values on a grid of
    /// its own. The window is left-open, as every range selector: a sample at <paramref name="from"/> is not in it.
    /// </summary>
    public async Task<SamplesResult> SamplesAsync(string selector, double from, double to, CancellationToken ct)
    {
        var window = Math.Max(1, (int)Math.Ceiling(to - from));
        var (data, error) = await QueryDataAsync(
            $"query={Uri.EscapeDataString($"{selector}[{window}s]")}&time={to.ToString("0.###", CultureInfo.InvariantCulture)}", ct);

        if (data is not { } result)
            return new SamplesResult([], error);

        var series = new List<Samples>();

        foreach (var item in result.GetProperty("result").EnumerateArray())
            series.Add(new Samples(Labels(item), item.GetProperty("values").EnumerateArray()
                .Select(point => (point[0].GetDouble(), Value(point[1])))
                .ToList()));

        return new SamplesResult(series, null);
    }

    /// <summary>The alerting rules of a group as Prometheus has loaded them; null if it has no such group.</summary>
    public async Task<LoadedGroup?> AlertRulesAsync(string group, CancellationToken ct)
    {
        string text;
        try
        {
            text = await http.GetStringAsync("api/v1/rules?type=alert", ct);
        }
        catch (Exception e) when (e is HttpRequestException || (e is TaskCanceledException && !ct.IsCancellationRequested))
        {
            throw new DrillException($"Prometheus at {Address} did not give its rules: {e.Message}");
        }

        using var json = JsonDocument.Parse(text);
        foreach (var loaded in json.RootElement.GetProperty("data").GetProperty("groups").EnumerateArray())
        {
            if (loaded.GetProperty("name").GetString() != group)
                continue;
            return new LoadedGroup(loaded.GetProperty("interval").GetDouble(), loaded.GetProperty("rules").EnumerateArray()
                .Select(r => new LoadedRule(
                    r.GetProperty("name").GetString()!,
                    r.GetProperty("query").GetString()!,
                    r.TryGetProperty("duration", out var duration) ? duration.GetDouble() : 0,
                    r.GetProperty("health").GetString()!,
                    r.TryGetProperty("lastError", out var lastError) && lastError.GetString() is { Length: > 0 } message ? message : null))
                .ToList());
        }
        return null;
    }

    public void Dispose()
    {
        http.Dispose();
        portForward?.Dispose();
    }

    private async Task WaitReadyAsync(TimeSpan limit, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + limit;
        var why = "";
        while (DateTime.UtcNow < deadline)
        {
            if (portForward is { HasExited: true })
            {
                // Its last words may still be on their way from the reader threads to the log.
                await Task.Delay(TimeSpan.FromMilliseconds(500), ct);
                throw new DrillException($"kubectl port-forward to {Service} exited: {LastLine(portForwardLog!)} ({portForwardLog})");
            }
            try
            {
                using var response = await http.GetAsync("-/ready", ct);

                if (response.IsSuccessStatusCode)
                    return;

                why = $"{(int)response.StatusCode} {response.ReasonPhrase}";
            }
            catch (Exception e) when (e is HttpRequestException || (e is TaskCanceledException && !ct.IsCancellationRequested))
            {
                // The port-forward has not opened its port yet.
                why = e.Message;
            }
            await Task.Delay(TimeSpan.FromSeconds(1), ct);
        }
        throw new DrillException($"Prometheus at {Address} is not ready after {limit.TotalSeconds:0} s: {why}");
    }

    // The data of a query: an instant query, or a range selector, which comes back as a matrix of raw samples.
    /// <summary>
    /// One expression of a single series on a grid of <paramref name="step"/> seconds from <paramref name="from"/> to
    /// <paramref name="to"/>, seconds since 1970: (time, value) for the moments it had a value. Several series are
    /// summed per moment; an expression is expected to aggregate them itself.
    /// </summary>
    public async Task<(IReadOnlyList<(double Time, double Value)> Points, string? Error)> RangeAsync(
        string expression, double from, double to, double step, CancellationToken ct)
    {
        string Time(double seconds) => seconds.ToString("0.###", CultureInfo.InvariantCulture);

        var (data, error) = await QueryDataAsync(
            $"query={Uri.EscapeDataString(expression)}&start={Time(from)}&end={Time(to)}&step={Time(step)}", ct, "query_range");

        if (data is not { } result)
            return ([], error);

        var points = new SortedDictionary<double, double>();
        foreach (var item in result.GetProperty("result").EnumerateArray())
            foreach (var point in item.GetProperty("values").EnumerateArray())
            {
                var time = point[0].GetDouble();
                points[time] = points.GetValueOrDefault(time) + Value(point[1]);
            }
        return (points.Select(p => (p.Key, p.Value)).ToList(), null);
    }

    private async Task<(JsonElement? Data, string? Error)> QueryDataAsync(string parameters, CancellationToken ct, string endpoint = "query")
    {
        string text;
        try
        {
            using var response = await http.GetAsync($"api/v1/{endpoint}?" + parameters, ct);
            text = await response.Content.ReadAsStringAsync(ct);
        }
        catch (Exception e) when (e is HttpRequestException || (e is TaskCanceledException && !ct.IsCancellationRequested))
        {
            return (null, $"no answer from Prometheus: {e.Message}");
        }

        // A bad expression is a 400 with the same envelope: {"status": "error", "error": "..."}.
        using var json = JsonDocument.Parse(text);
        var root = json.RootElement;
        if (root.GetProperty("status").GetString() != "success")
            return (null, root.TryGetProperty("error", out var error) ? error.GetString() : text);
        return (root.GetProperty("data").Clone(), null);
    }

    private static Dictionary<string, string> Labels(JsonElement series) =>
        series.GetProperty("metric").EnumerateObject().ToDictionary(l => l.Name, l => l.Value.GetString() ?? "");

    // "NaN" parses as is; Prometheus writes infinities as "+Inf" and "-Inf", which .NET does not read.
    private static double Value(JsonElement value) => value.GetString() switch
    {
        "+Inf" => double.PositiveInfinity,
        "-Inf" => double.NegativeInfinity,
        var text => double.Parse(text!, NumberStyles.Float, CultureInfo.InvariantCulture),
    };

    // The log is still open for writing by the process's readers: read it shared.
    private static string LastLine(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? "no output";
        }
        catch (IOException e)
        {
            return e.Message;
        }
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
