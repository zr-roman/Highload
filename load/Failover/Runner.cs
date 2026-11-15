using System.Diagnostics;
using System.Globalization;

namespace Failover;

/// <summary>A check after the run: what it looked at, whether it held, and what was seen.</summary>
public sealed record Check(string Name, bool Ok, string Detail);

/// <summary>When the node went down and came back, from the start of the load.</summary>
public sealed class Outage(FailoverOptions options, Stopwatch clock)
{
    public TimeSpan? Stopped { get; private set; }
    public TimeSpan? Started { get; private set; }

    public async Task RunAsync(CancellationToken ct)
    {
        await Task.Delay(TimeSpan.FromSeconds(options.BeforeSeconds), ct);
        Console.WriteLine($"Stopping {options.NodeName}");
        var stop = await Shell.RunAsync("docker", ["stop", options.NodeName], TimeSpan.FromSeconds(60), ct);
        if (!stop.Ok)
            throw new FailoverException($"docker stop {options.NodeName} failed: {stop.Errors}");
        Stopped = clock.Elapsed;
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(options.DownSeconds), ct);
        }
        finally
        {
            // Started again even when the run is stopped halfway: a node left down would break the next run.
            Console.WriteLine($"Starting {options.NodeName} again");
            await Shell.RunAsync("docker", ["start", options.NodeName], TimeSpan.FromSeconds(60));
            Started = clock.Elapsed;
        }
    }
}

/// <summary>A run from start to report: placement, load and outage under sampling, recovery, checks.</summary>
public static class Runner
{
    public static async Task RunAsync(FailoverOptions options, CancellationToken ct)
    {
        if (!File.Exists("load/payments.js"))
            throw new FailoverException("Run it from the root of the repository: load/payments.js is not here");

        // The samplers start kubectl several times a second; reading its output holds threads of the pool on Windows.
        ThreadPool.SetMinThreads(64, 64);

        var reports = Path.Combine("load", "reports", $"failover-{DateTime.Now:yyyyMMdd-HHmmss}");
        Directory.CreateDirectory(reports);
        var commit = await Shell.RunAsync("git", ["rev-parse", "--short=12", "HEAD"]);
        var dirty = await Shell.RunAsync("git", ["status", "--porcelain", "--untracked-files=no"]);

        Console.WriteLine($"Scenario {FailoverOptions.ScenarioName(options.Scenario)}, node {options.NodeName}: preparing the stand");
        await using var probe = await KafkaProbe.StartAsync(ct);
        var placement = await Placement.PrepareAsync(options, ct);
        Console.WriteLine($"PostgreSQL primary {placement.PgPrimary} ({placement.PgPrimaryNode}); outbox relay {placement.OutboxLeaderPod}; " +
                          $"broker {placement.BrokerOnNode} on the node leads {placement.PartitionsLedOnNode} partitions");

        var startedAt = await Cluster.PgNowAsync(placement.PgPrimary) ?? throw new FailoverException("Cannot read the time of PostgreSQL");
        using var traefik = new Traefik(reports);
        await Task.Delay(TimeSpan.FromSeconds(3), ct);
        var before = await traefik.CountsAsync();

        var clock = Stopwatch.StartNew();
        var sampler = new Sampler(options, placement, clock);
        var outage = new Outage(options, clock);
        using var sampling = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var samplingTask = sampler.RunAsync(sampling.Token);
        var outageTask = outage.RunAsync(ct);

        Console.WriteLine($"Load: {options.Rate} payments a second for {options.LoadSeconds} s; the node goes down at {options.BeforeSeconds} s for {options.DownSeconds} s");
        var k6 = await K6.RunAsync(options, reports, ct);
        await outageTask;
        var loadEnded = clock.Elapsed;
        var after = await traefik.CountsAsync();

        Console.WriteLine($"Load over. Waiting up to {options.SettleSeconds} s for the stand to recover");
        var settled = await SettleAsync(sampler, loadEnded, TimeSpan.FromSeconds(options.SettleSeconds), clock, ct);
        sampling.Cancel();
        await samplingTask;

        var checks = await ChecksAsync(options, placement, sampler, outage, k6, startedAt, settled);
        checks.Add(await BrokerRestartsCheckAsync(placement, reports));
        sampler.WriteCsv(Path.Combine(reports, "samples.csv"));

        var report = new Report(options, placement, outage, sampler, k6, Attempts(before, after), checks,
            commit.Ok ? commit.Output : null, dirty.Ok && dirty.Output.Length > 0, DateTimeOffset.Now - clock.Elapsed);
        var path = Path.Combine(reports, "report.md");
        File.WriteAllText(path, report.Markdown());

        Console.WriteLine();
        foreach (var check in checks)
            Console.WriteLine($"{(check.Ok ? "ok  " : "FAIL")}  {check.Name}: {check.Detail}");
        Console.WriteLine($"Report: {path}");
        if (checks.Any(c => !c.Ok))
            throw new FailoverException("Some checks failed, see the report");
    }

    // Recovered: the node Ready, the outbox empty, every partition back in full ISR, the Ledger caught up.
    private static async Task<bool> SettleAsync(Sampler sampler, TimeSpan loadEnded, TimeSpan limit, Stopwatch clock, CancellationToken ct)
    {
        var deadline = clock.Elapsed + limit;
        while (clock.Elapsed < deadline)
        {
            if (sampler.Last is { } s && s.At > loadEnded && s.NodeReady == true && s.Outbox == 0
                && sampler.LastKafka is { } k && k.At > loadEnded && k.UnderReplicated == 0 && k.Lag == 0)
                return true;
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }
        return false;
    }

    private static async Task<List<Check>> ChecksAsync(
        FailoverOptions options, PlacementInfo placement, Sampler sampler, Outage outage, K6Result k6, string startedAt, bool settled)
    {
        var checks = new List<Check>();
        var samples = sampler.Samples;
        var last = sampler.Last;
        var lastKafka = sampler.LastKafka;

        var (ok, text) = await Cluster.InvariantsAsync();
        checks.Add(new Check("инварианты Ledger", ok, ok ? "все платежи сбалансированы, счета сходятся" : text));

        // RPO: every payment k6 was told about is in the base. More in the base is fine: a request that timed out on
        // the client side may have been written all the same.
        var primary = await Cluster.PgPrimaryAsync() ?? placement.PgPrimary;
        var stored = await Cluster.PsqlAsync(primary, $"select count(*) from payments where created_at >= '{startedAt}'");
        if (stored is [var count] && long.TryParse(count, CultureInfo.InvariantCulture, out var inBase))
            checks.Add(new Check("ни один подтверждённый платёж не потерян", inBase >= k6.Succeeded,
                $"в базе {inBase:N0}, успешных ответов k6 {k6.Succeeded:N0}"));
        else
            checks.Add(new Check("ни один подтверждённый платёж не потерян", false, "не удалось посчитать платежи в базе"));

        checks.Add(new Check("стенд восстановился", settled, settled
            ? "нода Ready, outbox пуст, все партиции в полном ISR, Ledger догнал"
            : $"за {options.SettleSeconds} с — нет: нода {last?.NodeReady}, outbox {last?.Outbox}, URP {lastKafka?.UnderReplicated}, lag {lastKafka?.Lag}"));

        if (options.Scenario == Scenario.KafkaBroker && outage is { Stopped: { } stopped, Started: { } restarted })
        {
            // The point of the scenario: another replica took over the outbox while the node was still down.
            var takeover = samples.FirstOrDefault(s => s.At > stopped && s.At < restarted
                && s.OutboxLeader is { } leader && leader != placement.OutboxLeaderPod);
            checks.Add(new Check("outbox перешёл к другой реплике, пока нода лежала", takeover is not null, takeover is not null
                ? $"`{takeover.OutboxLeader}` — лидер через {(takeover.At - stopped).TotalSeconds:0} с после остановки ноды"
                : "лидер не сменился до возвращения ноды: блокировку держало соединение мёртвого пода"));

            // Seen, not assumed: a probe that got no answer says nothing about the ISR.
            var duringOutage = sampler.KafkaSamples.Where(k => k.At > stopped && k.At < restarted && k.MinIsr is not null).ToList();
            var writable = duringOutage.Count > 0 && duringOutage.All(k => k.MinIsr >= 2);
            checks.Add(new Check("Kafka писала с двумя брокерами", writable, duringOutage.Count == 0
                ? "ни одного ответа Kafka во время отказа: состояние топика неизвестно"
                : $"наименьший ISR во время отказа — {duringOutage.Min(k => k.MinIsr)} ({duringOutage.Count} замеров), min.insync.replicas 2"));
        }
        return checks;
    }

    // Only the broker on the stopped node is expected to restart, and it is not counted: its pod is the same, and
    // the container restarts with the node. Any other restart is a broker that fell over: its last words are kept.
    private static async Task<Check> BrokerRestartsCheckAsync(PlacementInfo placement, string reports)
    {
        var now = await Placement.BrokerRestartsAsync();
        var fell = now.Where(b => b.Key != $"kafka-broker-{placement.BrokerOnNode}" && b.Value > placement.BrokerRestarts.GetValueOrDefault(b.Key))
            .Select(b => b.Key).ToList();
        foreach (var broker in fell)
        {
            var log = await Shell.Kubectl(TimeSpan.FromSeconds(30), "-n", Cluster.Namespace, "logs", broker, "--previous", "--tail=300");
            File.WriteAllText(Path.Combine(reports, $"{broker}-previous.log"), log.Output);
        }
        return new Check("другие брокеры не перезапускались", fell.Count == 0, fell.Count == 0
            ? "перезапустился только брокер на остановленной ноде"
            : $"перезапускались {string.Join(", ", fell)}: их последний лог — {string.Join(", ", fell.Select(b => $"`{b}-previous.log`"))}");
    }

    private static Dictionary<string, double>? Attempts(Dictionary<string, double>? before, Dictionary<string, double>? after) =>
        before is null || after is null
            ? null
            : after.ToDictionary(a => a.Key, a => a.Value - before.GetValueOrDefault(a.Key)).Where(a => a.Value > 0).ToDictionary();
}
