using System.Net.Sockets;
using Confluent.Kafka;
using Npgsql;

namespace Matrix;

/// <summary>What the report says about the stand besides its settings.</summary>
public sealed record StandInfo(string? Commit, bool UncommittedChanges, string K6Version, string? DockerPriority);

/// <summary>Checks before half an hour of load: whatever is missing stops the matrix at once, not after the first run.</summary>
public static class Preflight
{
    public static async Task<StandInfo> CheckAsync(
        string repository, StandSettings settings, NpgsqlDataSource payments, IAdminClient kafka, CancellationToken ct)
    {
        // A service left from a manual run, maybe a Debug build, would take the load instead of the matrix's own.
        foreach (var url in new[] { Stand.PaymentsApiUrl, Stand.LedgerUrl, Stand.BankUrl })
        {
            var uri = new Uri(url);
            if (await ListensAsync(uri.Host, uri.Port, ct))
                throw new MatrixException($"Port {uri.Port} is taken: stop what runs there, the matrix starts the stand itself.");
        }

        try
        {
            kafka.GetMetadata(TimeSpan.FromSeconds(5));
        }
        catch (KafkaException e)
        {
            throw new MatrixException($"Kafka does not answer at {settings.Kafka} ({e.Error.Reason}): docker compose up -d");
        }

        try
        {
            await using var connection = await payments.OpenConnectionAsync(ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // Not only NpgsqlException: a PGPASSFILE that names a missing file, for one, fails as FileNotFoundException.
            throw new MatrixException($"Postgres of Payments.Api: {e.Message}");
        }

        var (redisHost, redisPort) = settings.RedisEndpoint;
        if (!await ListensAsync(redisHost, redisPort, ct))
            throw new MatrixException($"Redis does not answer at {redisHost}:{redisPort}: docker compose up -d. Without it the limiter lets everything through.");

        if (!await ListensAsync("localhost", 4317, ct))
            Console.WriteLine("Warning: the dashboard does not answer on port 4317, so ledger.payment.delay and ledger.batch.size of the runs will be lost.");

        // "k6 v1.1.0 (commit/..., go1.24.4, windows/amd64)": the version is enough.
        var k6 = await Processes.ReadAsync("k6", "version") ?? throw new MatrixException("k6 not found: winget install k6 --source winget");

        var head = await Processes.ReadAsync("git", "-C", repository, "rev-parse", "--short", "HEAD");
        var changes = await Processes.ReadAsync("git", "-C", repository, "status", "--porcelain");

        return new StandInfo(head, changes is { Length: > 0 }, k6.Split(" (")[0], await CheckDockerPriorityAsync(settings.KafkaPort));
    }

    /// <summary>
    /// In efficiency mode Windows runs Docker's relay of the Kafka port at Idle priority, and under load the relay nearly stops
    /// (docs/load-testing.md, "Асинхронная часть"). The command is the one from README, step 8. Null: nothing was checked.
    /// </summary>
    private static async Task<string?> CheckDockerPriorityAsync(int kafkaPort)
    {
        if (!OperatingSystem.IsWindows())
            return null;

        var output = await Processes.ReadAsync("powershell", "-NoProfile", "-Command",
            $"(Get-Process -Id (Get-NetTCPConnection -LocalPort {kafkaPort} -State Listen).OwningProcess).PriorityClass");
        // One line per listening socket, IPv4 and IPv6 may both be there.
        var priorities = output?.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Distinct().ToList() ?? [];
        if (priorities.Count == 0)
        {
            Console.WriteLine("Warning: could not check the priority of Docker's process (README, step 8).");
            return null;
        }

        var priority = string.Join(", ", priorities);
        if (priority != "Normal")
            throw new MatrixException(
                $"The process that listens on port {kafkaPort} for Docker runs at priority {priority}, not Normal: under load it nearly " +
                "stops relaying Kafka (README, step 8). Turn off efficiency mode for Docker in Task Manager and start again.");
        return priority;
    }

    private static async Task<bool> ListensAsync(string host, int port, CancellationToken ct)
    {
        using var client = new TcpClient();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            await client.ConnectAsync(host, port, timeout.Token);
            return true;
        }
        catch (Exception e) when (e is SocketException || (e is OperationCanceledException && !ct.IsCancellationRequested))
        {
            return false;
        }
    }
}
