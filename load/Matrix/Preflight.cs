using System.Net;
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

        // "k6 v1.1.0 (commit/..., go1.24.4, windows/amd64)", on Windows "k6.exe v2.2.0 (...)": the version is enough.
        var k6 = await Processes.ReadAsync("k6", "version") ?? throw new MatrixException("k6 not found: winget install k6 --source winget");

        var head = await Processes.ReadAsync("git", "-C", repository, "rev-parse", "--short", "HEAD");
        var changes = await Processes.ReadAsync("git", "-C", repository, "status", "--porcelain");

        return new StandInfo(head, changes is { Length: > 0 }, k6.Split(" (")[0].Replace("k6.exe", "k6"), await CheckDockerPriorityAsync(settings.KafkaPort));
    }

    /// <summary>
    /// In efficiency mode Windows runs Docker's relay of the Kafka port at Idle priority, and under load the relay nearly stops
    /// (docs/load-testing.md, "Асинхронная часть"); README, step 8, checks it by hand. Returns what the report shows:
    /// the processes that listen on the port, with their priorities. Null: nothing was checked.
    /// </summary>
    private static async Task<string?> CheckDockerPriorityAsync(int kafkaPort)
    {
        if (!OperatingSystem.IsWindows())
            return null;

        // A line "name=priority" for every process that listens on the port. Process 0 is left out: it is the System
        // Idle Process, which Windows names as the owner of a socket it does not attribute to a process, and it has no
        // priority. The priority is read by Select-Object, as in the command of README: where Windows does not let it be
        // read, as for a process of another user, Select-Object leaves it empty instead of failing.
        var result = await Processes.RunCommandAsync("powershell", "-NoProfile", "-Command",
            $"Get-NetTCPConnection -LocalPort {kafkaPort} -State Listen -ErrorAction SilentlyContinue" +
            " | Select-Object -ExpandProperty OwningProcess -Unique" +
            " | Where-Object { $_ -ne 0 }" +
            " | ForEach-Object { Get-Process -Id $_ -ErrorAction SilentlyContinue }" +
            " | Select-Object ProcessName, PriorityClass" +
            " | ForEach-Object { $_.ProcessName + '=' + $_.PriorityClass }");
        if (result is not { ExitCode: 0 })
        {
            var why = result?.Errors.Split('\n')[0].Trim() ?? "powershell not found";
            Console.WriteLine($"Warning: PowerShell failed, so the priority of Docker's process is not checked (README, step 8): {why}");
            return null;
        }

        var listeners = result.Output.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Split('=', 2))
            .Where(parts => parts.Length == 2 && parts[0].Length > 0) // a process that exited in between has no name
            .Select(parts => (Name: parts[0], Priority: parts[1]))
            .ToList();
        var shown = string.Join(", ", listeners.Select(listener => $"{listener.Name}: {(listener.Priority.Length > 0 ? listener.Priority : "?")}"));

        if (listeners.Count == 0)
        {
            Console.WriteLine($"Warning: no Windows process listens on port {kafkaPort}, so there is no priority to check (README, step 8).");
            return null;
        }
        if (listeners.Any(listener => listener.Priority.Length > 0 && listener.Priority != "Normal"))
            throw new MatrixException(
                $"Port {kafkaPort} is served by {shown}, not Normal: under load the relay nearly stops (README, step 8). " +
                "Turn off efficiency mode for it in Task Manager and start again.");
        var hidden = listeners.Where(listener => listener.Priority.Length == 0).Select(listener => listener.Name).ToList();
        if (hidden.Count > 0)
            Console.WriteLine($"Warning: Windows does not show the priority of {string.Join(", ", hidden)} on port {kafkaPort} (README, step 8).");
        return shown;
    }

    // Every address of the host at once, and the first that connects settles it. "localhost" is ::1 first, Docker publishes
    // its ports on 127.0.0.1 only, and on Windows a refused connection takes about 2 s: one address after another,
    // the right one comes too late.
    private static async Task<bool> ListensAsync(string host, int port, CancellationToken ct)
    {
        using var settled = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var attempts = (await Dns.GetHostAddressesAsync(host, ct))
            .Select(address => ConnectsAsync(address, port, settled.Token))
            .ToList();
        while (attempts.Count > 0)
        {
            var attempt = await Task.WhenAny(attempts);
            if (await attempt)
            {
                settled.Cancel(); // the other attempts are not needed any more
                return true;
            }
            attempts.Remove(attempt);
        }
        return false;
    }

    private static async Task<bool> ConnectsAsync(IPAddress address, int port, CancellationToken ct)
    {
        using var client = new TcpClient(address.AddressFamily);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            await client.ConnectAsync(address, port, timeout.Token);
            return true;
        }
        catch (Exception e) when (e is SocketException || (e is OperationCanceledException && !ct.IsCancellationRequested))
        {
            return false;
        }
    }
}
