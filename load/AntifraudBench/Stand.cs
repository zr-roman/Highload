using System.Diagnostics;
using System.Text.RegularExpressions;

namespace AntifraudBench;

/// <summary>Redis of docker compose and the antifraud container next to it, on the same network.</summary>
public sealed partial class Stand(string repository, string network)
{
    public const string RedisContainer = "payments-redis";
    public const string Image = "highload-antifraud:bench";
    public const string Container = "highload-antifraud-bench";

    // Inside the container. The service listens on 8000, as in the cluster (Dockerfile).
    public const int Port = 8000;

    public string Network { get; } = network;

    /// <summary>Starts Redis of docker compose if it is not running, and finds the network the containers share.</summary>
    public static async Task<Stand> PrepareAsync(string repository, CancellationToken ct)
    {
        await Shell.RunCheckedAsync("docker version", "docker", ["version", "--format", "{{.Server.Version}}"], ct);

        var running = await Shell.RunAsync("docker", ["inspect", "-f", "{{.State.Running}}", RedisContainer], ct);
        if (running.Output != "true")
        {
            Console.WriteLine($"Starting {RedisContainer}: docker compose up -d redis");
            await Shell.RunCheckedAsync("docker compose up -d redis", "docker", ["compose", "up", "-d", "redis"], ct, repository);
        }

        var networks = await Shell.RunCheckedAsync("docker inspect of Redis", "docker",
            ["inspect", "-f", "{{range $name, $_ := .NetworkSettings.Networks}}{{$name}} {{end}}", RedisContainer], ct);

        var network = networks.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()
            ?? throw new BenchException($"{RedisContainer} is on no network");

        return new Stand(repository, network);
    }

    public async Task BuildAsync(string logPath, CancellationToken ct)
    {
        Console.WriteLine($"Building {Image} (log: {logPath})");

        var result = await Shell.RunAsync("docker",
            ["build", "-t", Image, "-f", Path.Combine("src", "antifraud-service", "Dockerfile"), "."], ct, repository);

        await File.WriteAllTextAsync(logPath, result.Output + "\n" + result.Errors, ct);

        if (result.ExitCode != 0)
            throw new BenchException($"docker build failed (exit code {result.ExitCode}), see {logPath}");
    }

    public static async Task CheckImageAsync(CancellationToken ct)
    {
        var result = await Shell.RunAsync("docker", ["image", "inspect", "-f", "{{.Id}}", Image], ct);
        if (result.ExitCode != 0)
            throw new BenchException($"No image {Image}: run the bench once without --no-build");
    }

    /// <summary>
    /// The service with the given number of uvicorn processes, its Redis on the compose network. One process is uvicorn
    /// alone; more are a supervisor and that many processes it spawns.
    /// </summary>
    public async Task StartAsync(int workers, int redisTimeoutMs, int? maxInFlight, double cpus, CancellationToken ct)
    {
        await RemoveAsync();

        List<string> arguments =
        [
            "run", "-d", "--name", Container, "--network", Network,
            "-e", $"ANTIFRAUD_REDIS_URL=redis://{RedisContainer}:6379/0",
            "-e", $"ANTIFRAUD_REDIS_TIMEOUT_MS={redisTimeoutMs}",
        ];

        if (maxInFlight is { } limit)
            arguments.AddRange(["-e", $"ANTIFRAUD_MAX_IN_FLIGHT={limit}"]);

        if (cpus > 0)
            arguments.AddRange(["--cpus", cpus.ToString("0.##")]);

        arguments.AddRange(
        [
            Image,
            // --log-level warning: at info uvicorn writes a line per request, and the bench would measure the log.
            "uvicorn", "antifraud.main:app", "--host", "0.0.0.0", "--port", Port.ToString(),
            "--workers", workers.ToString(), "--log-level", "warning",
        ]);

        await Shell.RunCheckedAsync("docker run of antifraud", "docker", arguments, ct);
    }

    /// <summary>
    /// Waits until /healthz answers and every uvicorn process is alive. /healthz answers once one process listens,
    /// and a process can fail at startup and be restarted (on Windows one did, see docs/load-testing.md): the series
    /// starts only with all of them.
    /// </summary>
    public async Task WaitReadyAsync(int workers, CancellationToken ct)
    {
        var expectedSpawned = workers == 1 ? 0 : workers;
        var deadline = Stopwatch.StartNew();
        var spawned = -1;
        var healthy = false;

        while (deadline.Elapsed < TimeSpan.FromSeconds(60))
        {
            var running = await Shell.RunAsync("docker", ["inspect", "-f", "{{.State.Running}}", Container], ct);
            if (running.Output != "true")
            {
                var logs = await Shell.RunAsync("docker", ["logs", "--tail", "20", Container], ct);
                throw new BenchException($"The antifraud container has stopped:\n{logs.Errors}\n{logs.Output}");
            }

            var health = await Shell.RunAsync("docker",
                ["exec", Container, "python", "-c",
                 $"import urllib.request; urllib.request.urlopen('http://127.0.0.1:{Port}/healthz', timeout=1)"], ct);
            healthy = health.ExitCode == 0;

            // docker top runs ps in the VM of Docker, so the slim image needs no ps of its own.
            var top = await Shell.RunAsync("docker", ["top", Container], ct);
            spawned = top.Output.Split('\n').Count(line => line.Contains("spawn_main", StringComparison.Ordinal));

            if (healthy && spawned == expectedSpawned)
                return;

            await Task.Delay(500, ct);
        }

        throw new BenchException(
            $"Antifraud with {workers} processes is not ready in 60 s: /healthz {(healthy ? "answers" : "does not answer")}, " +
            $"{spawned} of {expectedSpawned} spawned processes alive (docker logs {Container})");
    }

    public async Task SaveLogsAsync(string path)
    {
        var logs = await Shell.RunAsync("docker", ["logs", Container], CancellationToken.None);
        await File.WriteAllTextAsync(path, logs.Output + "\n" + logs.Errors);
    }

    public async Task RemoveAsync() =>
        await Shell.RunAsync("docker", ["rm", "-f", Container], CancellationToken.None);

    /// <summary>Every step starts on an empty Redis: the steps start alike, and the keys after it are the step's own.</summary>
    public async Task FlushRedisAsync(CancellationToken ct) =>
        await Shell.RunCheckedAsync("FLUSHDB of Redis", "docker", ["exec", RedisContainer, "redis-cli", "flushdb"], ct);

    public async Task<RedisUsage> ReadRedisAsync(CancellationToken ct)
    {
        var keys = await Shell.RunCheckedAsync("DBSIZE of Redis", "docker", ["exec", RedisContainer, "redis-cli", "dbsize"], ct);
        var memory = await Shell.RunCheckedAsync("INFO of Redis", "docker", ["exec", RedisContainer, "redis-cli", "info", "memory"], ct);
        var used = UsedMemory().Match(memory);
        return new RedisUsage(long.Parse(keys), used.Success ? long.Parse(used.Groups[1].Value) : 0);
    }

    [GeneratedRegex(@"^used_memory:(\d+)", RegexOptions.Multiline)]
    private static partial Regex UsedMemory();
}

public sealed record RedisUsage(long Keys, long UsedBytes);
