using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Matrix;

/// <summary>
/// The antifraud service of --antifraud: its image from src/antifraud-service/Dockerfile, a container on the network of
/// docker compose next to its Redis, published on 127.0.0.1:5400 for Payments.Api, which runs on the host. A Linux
/// container and not uvicorn on Windows: there the clock coarsens the timeout of Redis, and several processes start
/// unreliably (docs/load-testing.md, «Этап 4: антифрод, первый замер»).
/// </summary>
public sealed partial class AntifraudContainer(string repository, string reports, int workers, int velocityLimit) : IDisposable
{
    /// <summary>
    /// The port of the service in README, by its IPv4 address: Docker publishes it on 127.0.0.1 alone, and localhost
    /// of Antifraud:BaseAddress is ::1 first, refused in 5-55 ms on Windows before .NET tries 127.0.0.1.
    /// </summary>
    public const string Url = "http://127.0.0.1:5400";

    public const string Container = "highload-antifraud-matrix";

    private const string Image = "highload-antifraud:matrix";

    // The container of docker-compose.yml. The limiter of Payments.Api keeps its buckets in database 0; the antifraud
    // gets database 1, so a flush before a repeat takes its keys alone.
    private const string RedisContainer = "payments-redis";
    private const int RedisDatabase = 1;

    // Inside the container, as in the cluster (Dockerfile).
    private const int Port = 8000;


    private static readonly TimeSpan ReadyTimeout = TimeSpan.FromSeconds(60);
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(2) };

    private LoggedProcess? stats;
    private readonly List<(DateTimeOffset At, double Percent)> cpu = [];

    public int Workers => workers;

    public bool Started { get; private set; }

    /// <summary>A container left by a matrix that was killed holds the port: gone before the checks look at it.</summary>
    public static async Task RemoveAsync() => await Processes.RunCommandAsync("docker", "rm", "-f", Container);

    public async Task BuildAsync(CancellationToken ct)
    {
        var log = Path.Combine(reports, "build-antifraud.log");
        var build = Processes.Info("docker", ["build", "-t", Image, "-f", Path.Combine("src", "antifraud-service", "Dockerfile"), "."], repository);
        if (await Processes.RunAsync(build, log, ct) != 0)
            throw new MatrixException($"docker build of the antifraud failed, see {log}");
    }

    /// <summary>
    /// The service with <see cref="Workers"/> uvicorn processes, waited for until /healthz answers through the published
    /// port and every process is alive: /healthz answers once one of them listens.
    /// </summary>
    public async Task StartAsync(CancellationToken ct)
    {
        await RemoveAsync();

        var networks = await Processes.RunCommandAsync("docker", "inspect", "-f",
            "{{range $name, $_ := .NetworkSettings.Networks}}{{$name}} {{end}}", RedisContainer);
        var network = networks is { ExitCode: 0 } ? networks.Output.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() : null;
        if (network is null)
            throw new MatrixException($"--antifraud: no network of {RedisContainer}: docker compose up -d");

        var run = await Processes.RunCommandAsync("docker",
            "run", "-d", "--name", Container, "--network", network, "-p", $"127.0.0.1:5400:{Port}",
            "-e", $"ANTIFRAUD_REDIS_URL=redis://{RedisContainer}:6379/{RedisDatabase}",
            "-e", $"ANTIFRAUD_VELOCITY_LIMIT={velocityLimit}",
            Image,
            // --log-level warning: at info uvicorn writes a line per request, and the matrix would measure the log.
            "uvicorn", "antifraud.main:app", "--host", "0.0.0.0", "--port", $"{Port}", "--workers", $"{workers}", "--log-level", "warning");
        if (run is not { ExitCode: 0 })
            throw new MatrixException($"docker run of the antifraud failed: {run?.Errors ?? "docker not found"}");

        // One process is uvicorn alone; more are a supervisor and that many processes it spawns.
        var expected = workers == 1 ? 0 : workers;
        var waited = Stopwatch.StartNew();
        while (waited.Elapsed < ReadyTimeout)
        {
            await EnsureRunningAsync();
            var top = await Processes.RunCommandAsync("docker", "top", Container);
            var spawned = top?.Output.Split('\n').Count(line => line.Contains("spawn_main", StringComparison.Ordinal)) ?? 0;
            if (spawned == expected && await HealthyAsync(ct))
            {
                Started = true;
                return;
            }

            await Task.Delay(500, ct);
        }

        throw new MatrixException($"The antifraud with {workers} processes is not ready in {ReadyTimeout.TotalSeconds:0} s: docker logs {Container}");
    }

    /// <summary>A container that has stopped would leave every check without a decision, and the repeat meaningless.</summary>
    public async Task EnsureRunningAsync()
    {
        var running = await Processes.RunCommandAsync("docker", "inspect", "-f", "{{.State.Running}}", Container);
        if (running is not { Output: "true" })
        {
            await SaveLogsAsync();
            throw new MatrixException($"The antifraud container has stopped, see {Path.Combine(reports, "antifraud.log")}");
        }
    }

    /// <summary>
    /// Every repeat starts with no keys of the antifraud: the decisions of the repeats before would otherwise pile up
    /// in Redis, about 650 bytes a check, a gigabyte over a matrix (docs/load-testing.md, «Этап 4: антифрод, первый замер»).
    /// </summary>
    public async Task FlushAsync()
    {
        var flush = await Processes.RunCommandAsync("docker", "exec", RedisContainer, "redis-cli", "-n", $"{RedisDatabase}", "flushdb");
        if (flush is not { ExitCode: 0 })
            throw new MatrixException($"FLUSHDB of database {RedisDatabase} of Redis failed: {flush?.Errors}");
    }

    /// <summary>docker stats of the container, about once a second, until <see cref="StopSampling"/>. 100 % is one core.</summary>
    public void StartSampling(string label)
    {
        lock (cpu)
            cpu.Clear();

        stats = new LoggedProcess("docker stats",
            Processes.Info("docker", ["stats", "--format", "{{.CPUPerc}}", Container]),
            Path.Combine(reports, $"run-{label}-antifraud-cpu.log"),
            line =>
            {
                // Without a terminal docker stats still clears the screen before every frame: escape sequences first.
                var match = Percent().Match(Escape().Replace(line, ""));
                if (match.Success)
                    lock (cpu)
                        cpu.Add((DateTimeOffset.Now, double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture)));
            });
    }

    /// <summary>
    /// The median processor of the container from the start of the sampling to <paramref name="until"/>, the end of the
    /// load: the catch-up after it, sampled as well, would pull the median down. In cores; null without samples.
    /// </summary>
    public double? StopSampling(DateTimeOffset until)
    {
        stats?.Dispose();
        stats = null;
        lock (cpu)
        {
            // The first sample is taken before docker stats has two readings to compare, often 0: dropped.
            var values = cpu.Skip(1).Where(sample => sample.At <= until).Select(sample => sample.Percent).Order().ToList();
            return values.Count == 0 ? null : values[values.Count / 2] / 100;
        }
    }

    public void Dispose()
    {
        stats?.Dispose();
        if (!Started)
            return;

        SaveLogsAsync().GetAwaiter().GetResult();
        RemoveAsync().GetAwaiter().GetResult();
    }

    private async Task SaveLogsAsync()
    {
        var logs = await Processes.RunCommandAsync("docker", "logs", Container);
        await File.WriteAllTextAsync(Path.Combine(reports, "antifraud.log"), $"{logs?.Output}\n{logs?.Errors}");
    }

    private static async Task<bool> HealthyAsync(CancellationToken ct)
    {
        try
        {
            using var response = await Http.GetAsync($"{Url}/healthz", ct);
            return response.IsSuccessStatusCode;
        }
        catch (Exception e) when (e is HttpRequestException || (e is TaskCanceledException && !ct.IsCancellationRequested))
        {
            return false; // the port is not published yet, or no answer in 2 s
        }
    }

    [GeneratedRegex(@"^([\d.]+)%")]
    private static partial Regex Percent();

    [GeneratedRegex(@"\x1b\[[0-9;]*[A-Za-z]")]
    private static partial Regex Escape();
}
