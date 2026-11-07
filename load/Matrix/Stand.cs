using System.Diagnostics;

namespace Matrix;

/// <summary>
/// The processes of the stand, started as README, step 8, says for load runs: the bank emulator with its defaults
/// and both services from the Release build with 1% of traces. Kafka, Redis, the dashboard and Postgres it only uses.
/// </summary>
public sealed class Stand(string repository, string reports) : IDisposable
{
    // The launch profiles of the services and the emulator's default BANK_ADDR.
    public const string PaymentsApiUrl = "http://localhost:5100";
    public const string LedgerUrl = "http://localhost:5200";
    public const string BankUrl = "http://localhost:5300";

    // go run compiles the emulator before it starts.
    private static readonly TimeSpan StartTimeout = TimeSpan.FromMinutes(3);

    private static readonly HttpClient Http = new();

    // 1% of traces: a trace of every request is thousands of spans a second for the dashboard on the same laptop.
    // Metrics every second instead of every minute: the backlog gauges get a point a second on the dashboard.
    private static readonly Dictionary<string, string> Telemetry = new()
    {
        ["OTEL_TRACES_SAMPLER"] = "parentbased_traceidratio",
        ["OTEL_TRACES_SAMPLER_ARG"] = "0.01",
        ["OTEL_METRIC_EXPORT_INTERVAL"] = "1000",
    };

    private readonly List<DateTimeOffset> redisTimeouts = [];
    private LoggedProcess? bank, ledger, paymentsApi;

    /// <summary>Release builds of both services, so that dotnet run --no-build starts them together without building twice.</summary>
    public async Task BuildAsync(CancellationToken ct)
    {
        foreach (var project in new[] { "Ledger.Service", "Payments.Api" })
        {
            var log = Log($"build-{project}.log");
            var build = Processes.Info("dotnet", ["build", "-c", "Release", Path.Combine("src", project)], repository);
            if (await Processes.RunAsync(build, log, ct) != 0)
                throw new MatrixException($"The build of {project} failed, see {log}");
        }
    }

    /// <summary>
    /// Starts the emulator and the ledger if they are not running yet, and Payments.Api with the limiter on or off.
    /// A running Payments.Api is restarted: the setting is an environment variable, and a process reads those at its start.
    /// </summary>
    public async Task StartAsync(bool limiter, CancellationToken ct)
    {
        bank ??= new LoggedProcess("Bank emulator", Processes.Info("go", ["run", "."], Path.Combine(repository, "src", "bank-emulator")), Log("bank-emulator.log"));
        ledger ??= DotnetRun("Ledger.Service", Telemetry, "ledger.log");
        paymentsApi?.Dispose();
        paymentsApi = StartPaymentsApi(limiter);

        await Task.WhenAll(
            WaitHealthyAsync(bank, $"{BankUrl}/healthz", ct),
            WaitHealthyAsync(ledger, $"{LedgerUrl}/health", ct),
            WaitHealthyAsync(paymentsApi, $"{PaymentsApiUrl}/health", ct));
    }

    /// <summary>
    /// How many times in the window Payments.Api logged that Redis did not answer: after each, the limit is off
    /// for every merchant for 5 s or more (RedisCircuitBreaker).
    /// </summary>
    public int RedisTimeouts(DateTimeOffset from, DateTimeOffset to)
    {
        lock (redisTimeouts)
            return redisTimeouts.Count(at => at >= from && at <= to);
    }

    /// <summary>A process of the stand that has died would leave every run after it meaningless.</summary>
    public void EnsureRunning()
    {
        foreach (var process in new[] { bank, ledger, paymentsApi })
            if (process is { HasExited: true })
                throw new MatrixException($"{process.Name} has exited, see {process.LogPath}");
    }

    public void Dispose()
    {
        paymentsApi?.Dispose();
        ledger?.Dispose();
        bank?.Dispose();
    }

    private LoggedProcess StartPaymentsApi(bool limiter)
    {
        // Set either way: a value left in the shell is not passed on (Processes.IsStandSetting).
        var settings = new Dictionary<string, string>(Telemetry) { ["RateLimiting__Merchant__Enabled"] = limiter ? "true" : "false" };
        return DotnetRun("Payments.Api", settings, $"payments-api-limiter-{(limiter ? "on" : "off")}.log", line =>
        {
            if (line.Contains("Redis did not answer", StringComparison.Ordinal))
                lock (redisTimeouts)
                    redisTimeouts.Add(DateTimeOffset.Now);
        });
    }

    private LoggedProcess DotnetRun(string project, IReadOnlyDictionary<string, string> settings, string log, Action<string>? onLine = null) =>
        new(project,
            Processes.Info("dotnet", ["run", "--no-build", "-c", "Release", "--project", Path.Combine("src", project), "--launch-profile", "http"], repository, settings),
            Log(log),
            onLine);

    private string Log(string name) => Path.Combine(reports, name);

    private static async Task WaitHealthyAsync(LoggedProcess process, string url, CancellationToken ct)
    {
        var waited = Stopwatch.StartNew();
        while (!await IsHealthyAsync(url, ct))
        {
            if (process.HasExited)
                throw new MatrixException($"{process.Name} exited on start, see {process.LogPath}");
            if (waited.Elapsed > StartTimeout)
                throw new MatrixException($"{process.Name} did not answer at {url} in {StartTimeout.TotalMinutes:0} min, see {process.LogPath}");
            await Task.Delay(TimeSpan.FromMilliseconds(500), ct);
        }
    }

    private static async Task<bool> IsHealthyAsync(string url, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            using var response = await Http.GetAsync(url, timeout.Token);
            return response.IsSuccessStatusCode;
        }
        catch (Exception e) when (e is HttpRequestException || (e is OperationCanceledException && !ct.IsCancellationRequested))
        {
            return false; // not listening yet, or no answer in 2 s
        }
    }
}
