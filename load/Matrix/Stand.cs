using System.Diagnostics;

namespace Matrix;

/// <summary>
/// The processes of the stand, started as README, step 8, says for load runs: the bank emulator with its defaults
/// (or failing the hot merchant, --bank-fails-hot) and both services from the Release build with 1% of traces,
/// each in one instance or several; with --antifraud also the antifraud service in a container (AntifraudContainer).
/// Kafka, Redis, the dashboard and Postgres it only uses.
/// </summary>
public sealed class Stand(string repository, string reports, MatrixOptions options) : IDisposable
{
    // The launch profiles of the services and the emulator's default BANK_ADDR. /invariants is asked of the first Ledger.
    public const string LedgerUrl = "http://localhost:5200";
    public const string BankUrl = "http://localhost:5300";

    // The container of docker-compose.yml.
    private const string RedisContainer = "payments-redis";

    // go run compiles the emulator before it starts.
    private static readonly TimeSpan StartTimeout = TimeSpan.FromMinutes(3);

    private static readonly HttpClient Http = new();

    private static readonly Dictionary<string, string> ServiceSettings = new()
    {
        // 1% of traces: a trace of every request is thousands of spans a second for the dashboard on the same laptop.
        ["OTEL_TRACES_SAMPLER"] = "parentbased_traceidratio",
        ["OTEL_TRACES_SAMPLER_ARG"] = "0.01",
        // Metrics every second instead of every minute: the backlog gauges get a point a second on the dashboard.
        ["OTEL_METRIC_EXPORT_INTERVAL"] = "1000",
        // A time on every line of the logs, to match them with the windows of the repeats in the table. The formatter is named
        // on purpose: without a name the console logger reads the obsolete options and skips FormatterOptions.
        ["Logging__Console__FormatterName"] = "simple",
        ["Logging__Console__FormatterOptions__TimestampFormat"] = "HH:mm:ss.fff ",
    };

    private readonly List<DateTimeOffset> redisTimeouts = [];
    private readonly List<DateTimeOffset> unknownChecks = [];
    private readonly List<DateTimeOffset> failOpen = [];
    private readonly List<DateTimeOffset> circuitOpened = [];
    private readonly List<DateTimeOffset> recoveryPaused = [];
    private LoggedProcess? bank;
    private List<LoggedProcess> ledgers = [];
    private List<LoggedProcess> paymentsApis = [];

    public IReadOnlyList<string> PaymentsApiUrls { get; } = PaymentsApiUrlsOf(options.Instances);

    /// <summary>The antifraud container with --antifraud; null without it.</summary>
    public AntifraudContainer? Antifraud { get; } =
        options.Antifraud ? new AntifraudContainer(repository, reports, options.Workers, options.Velocity) : null;

    public IReadOnlyList<string> LedgerUrls { get; } = LedgerUrlsOf(options.Ledgers);

    /// <summary>
    /// The first instance of Payments.Api on the port of its launch profile, 5100, the others on the ports after it.
    /// One leader lock per job lets only one of them relay the outbox and recover payments (AdvisoryLeaderLock);
    /// the others take payments.
    /// </summary>
    public static IReadOnlyList<string> PaymentsApiUrlsOf(int instances) =>
        Enumerable.Range(0, instances).Select(instance => $"http://localhost:{5100 + instance}").ToList();

    /// <summary>
    /// The ledgers on 5200, 5201, ...: one consumer group, so Kafka splits the partitions between them, and a partition
    /// with all its payments goes to one of them.
    /// </summary>
    public static IReadOnlyList<string> LedgerUrlsOf(int ledgers) =>
        Enumerable.Range(0, ledgers).Select(instance => $"http://localhost:{5200 + instance}").ToList();

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

        if (Antifraud is not null)
            await Antifraud.BuildAsync(ct);
    }

    /// <summary>
    /// Starts the emulator and the ledgers if they are not running yet, and Payments.Api with the limiter on or off.
    /// A running Payments.Api is restarted: the setting is an environment variable, and a process reads those at its start.
    /// </summary>
    public async Task StartAsync(bool limiter, CancellationToken ct)
    {
        // --bank-fails-hot: the bank fails the hot merchant alone.
        var bankSettings = options.BankFailsHot ? new Dictionary<string, string> { ["BANK_ERROR_MERCHANT"] = $"{K6.HotMerchant}" } : null;
        bank ??= new LoggedProcess("Bank emulator",
            Processes.Info("go", ["run", "."], Path.Combine(repository, "src", "bank-emulator"), bankSettings), Log("bank-emulator.log"));

        if (ledgers.Count == 0)
            ledgers = LedgerUrls.Select(StartLedger).ToList();

        // Before Payments.Api: its first payments already go to the antifraud. It stays up across the restarts of
        // Payments.Api, as the ledgers do.
        if (Antifraud is { Started: false })
            await Antifraud.StartAsync(ct);

        foreach (var instance in paymentsApis)
            instance.Dispose();

        paymentsApis = PaymentsApiUrls.Select((url, instance) => StartPaymentsApi(limiter, instance, url)).ToList();

        await Task.WhenAll([
            WaitHealthyAsync(bank, $"{BankUrl}/healthz", ct),
            .. ledgers.Select((instance, i) => WaitHealthyAsync(instance, $"{LedgerUrls[i]}/health", ct)),
            .. paymentsApis.Select((instance, i) => WaitHealthyAsync(instance, $"{PaymentsApiUrls[i]}/health", ct)),
        ]);
    }

    /// <summary>Before the matrix, not in the middle of its first repeat: --redis-pause needs docker and the container.</summary>
    public async Task EnsureRedisCanPauseAsync(CancellationToken ct)
    {
        var ping = Processes.Info("docker", ["exec", RedisContainer, "redis-cli", "PING"]);
        var log = Log("redis-pause.log");
        if (await Processes.RunAsync(ping, log, ct) != 0)
            throw new MatrixException($"--redis-pause: docker exec {RedisContainer} redis-cli failed, see {log}");
    }

    /// <summary>
    /// Holds every command to Redis for <paramref name="duration"/>, as a Redis that hangs: CLIENT PAUSE, after
    /// <paramref name="after"/>. The limiter of each instance times out and limits merchants on its own until a probe
    /// gets an answer.
    /// </summary>
    public async Task PauseRedisAsync(TimeSpan after, TimeSpan duration, CancellationToken ct)
    {
        await Task.Delay(after, ct);
        var pause = Processes.Info("docker", ["exec", RedisContainer, "redis-cli", "CLIENT", "PAUSE", $"{(long)duration.TotalMilliseconds}"]);
        var log = Log("redis-pause.log");

        if (await Processes.RunAsync(pause, log, ct) != 0)
            throw new MatrixException($"Redis could not be paused, see {log}");

        Console.WriteLine($"  {DateTime.Now:HH:mm:ss}  Redis paused for {duration.TotalSeconds:0} s");
    }

    /// <summary>
    /// How many times in the window Payments.Api logged that Redis did not answer: a line per outage and instance; for
    /// that time each instance limits merchants on its own (LocalMerchantLimiter). Before step 4a of stage 3 the limit
    /// went off instead, and the breaker flapped, with many lines per outage, sometimes out of order.
    /// </summary>
    public int RedisTimeouts(DateTimeOffset from, DateTimeOffset to)
    {
        lock (redisTimeouts)
            return redisTimeouts.Count(at => at >= from && at <= to);
    }

    /// <summary>
    /// What the logs of Payments.Api, all its instances, tell of the checks of the antifraud in the window. A check
    /// without a decision because of a 5xx or a timeout is a warning of HttpAntifraud; one while the circuit is open is
    /// logged at Debug, so not at all, and the openings of the circuit are counted instead.
    /// </summary>
    public AntifraudEvents AntifraudEvents(DateTimeOffset from, DateTimeOffset to)
    {
        int Count(List<DateTimeOffset> events)
        {
            lock (events)
                return events.Count(at => at >= from && at <= to);
        }

        return new AntifraudEvents(Count(unknownChecks), Count(failOpen), Count(circuitOpened), Count(recoveryPaused));
    }

    /// <summary>A process of the stand that has died would leave every run after it meaningless.</summary>
    public void EnsureRunning()
    {
        LoggedProcess?[] processes = [bank, .. ledgers, .. paymentsApis];
        foreach (var process in processes)
            if (process is { HasExited: true })
                throw new MatrixException($"{process.Name} has exited, see {process.LogPath}");
    }

    public void Dispose()
    {
        foreach (var instance in paymentsApis)
            instance.Dispose();

        foreach (var instance in ledgers)
            instance.Dispose();

        bank?.Dispose();
        Antifraud?.Dispose();
    }

    private LoggedProcess StartPaymentsApi(bool limiter, int instance, string url)
    {
        // Set either way: a value left in the shell is not passed on (Processes.IsStandSetting).
        var settings = new Dictionary<string, string>(ServiceSettings)
        {
            ["RateLimiting__Merchant__Enabled"] = limiter ? "true" : "false",
            // Without --antifraud it decides in the process: the matrix measures the way of a payment and stays comparable
            // with the matrices before the saga. With it, the service of the container: the cost of the check in the way.
            ["Antifraud__Mode"] = Antifraud is null ? "Fake" : "Http",
        };

        if (Antifraud is not null)
        {
            settings["Antifraud__BaseAddress"] = $"{AntifraudContainer.Url}/";
            // The budget of the stand in the cluster, not the 50 and 150 ms of appsettings.json, for the same reason
            // (deploy/k8s/payments-api.yaml): on a laptop busy with the whole stand an attempt of 50 ms is often cut
            // off, a cut-off closes its HTTP/1.1 connection, and the next attempt waits for a new one. Through the port
            // of Docker Desktop a new connection took 7 ms at the median and 44 ms at p99 with the stand idle, against
            // 4 and 12 ms on a kept one; in the trial matrix of 23.11 with 50 ms the circuit was open most of the time.
            settings["Antifraud__Resilience__AttemptTimeout__Timeout"] = "00:00:00.150";
            settings["Antifraud__Resilience__TotalRequestTimeout__Timeout"] = "00:00:00.400";
        }

        // --hot-quota: the override of RateLimiting:Merchant:Overrides, as appsettings.json would carry it.
        if (options.HotQuota > 0)
        {
            settings[$"RateLimiting__Merchant__Overrides__{K6.HotMerchant}__TokensPerSecond"] = $"{options.HotQuota}";
            settings[$"RateLimiting__Merchant__Overrides__{K6.HotMerchant}__Burst"] = $"{options.HotBurst}";
        }

        // The first instance keeps the names it had when there was only one.
        var number = instance == 0 ? "" : $" {instance + 1}";
        var log = $"payments-api{number.Replace(' ', '-')}-limiter-{(limiter ? "on" : "off")}.log";

        // --urls on the command line wins over the address of the launch profile.
        return DotnetRun("Payments.Api", settings, log, line =>
        {
            if (line.Contains("Redis did not answer", StringComparison.Ordinal))
                Add(redisTimeouts);
            // The lines of HttpAntifraud, PaymentSaga, Polly and PaymentRecovery; without --antifraud none comes.
            else if (line.Contains(", outcome unknown", StringComparison.Ordinal))
                Add(unknownChecks);
            else if (line.Contains("Antifraud gave no decision on payment", StringComparison.Ordinal))
                Add(failOpen);
            else if (line.Contains("EventName: 'OnCircuitOpened', Source: 'IAntifraud-standard", StringComparison.Ordinal))
                Add(circuitOpened);
            else if (line.Contains("Antifraud unavailable, recovery paused", StringComparison.Ordinal))
                Add(recoveryPaused);
        }, name: $"Payments.Api{number}", arguments: ["--urls", url]);

        static void Add(List<DateTimeOffset> events)
        {
            lock (events)
                events.Add(DateTimeOffset.Now);
        }
    }

    private LoggedProcess StartLedger(string url, int instance)
    {
        var settings = new Dictionary<string, string>(ServiceSettings);

        // --ledger-batch: Consumer:BatchSize, the most payments of a partition in one statement and one commit of offsets.
        if (options.LedgerBatch > 0)
            settings["Consumer__BatchSize"] = $"{options.LedgerBatch}";

        // The first instance keeps the names it had when there was only one.
        var number = instance == 0 ? "" : $" {instance + 1}";
        return DotnetRun("Ledger.Service", settings, $"ledger{number.Replace(' ', '-')}.log",
            name: $"Ledger.Service{number}", arguments: ["--urls", url]);
    }

    private LoggedProcess DotnetRun(
        string project, IReadOnlyDictionary<string, string> settings, string log, Action<string>? onLine = null,
        string? name = null, string[]? arguments = null)
    {
        // After --, the arguments go to the program, not to dotnet run.
        string[] program = arguments is null ? [] : ["--", .. arguments];
        return new(name ?? project,
            Processes.Info("dotnet", ["run", "--no-build", "-c", "Release", "--project", Path.Combine("src", project), "--launch-profile", "http", .. program], repository, settings),
            Log(log),
            onLine);
    }

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

/// <summary>
/// The checks of the antifraud in a window, from the logs of Payments.Api: without a decision for a 5xx or a timeout,
/// captured unchecked (fail open), openings of its circuit breaker, pauses of recovery for it.
/// </summary>
public sealed record AntifraudEvents(int Unknown, int FailOpen, int CircuitOpened, int RecoveryPaused);
