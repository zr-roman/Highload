using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Failover;

/// <summary>A pod, as the samples need it.</summary>
public sealed record PodInfo(string Name, string Node, string? Ip, bool Ready);

/// <summary>What one look at PostgreSQL saw: the outbox and who holds the leader locks of Payments.Api.</summary>
public sealed record PgState(long Outbox, int OutboxAgeSeconds, string? OutboxLeaderIp, string? RecoveryLeaderIp);

/// <summary>What one look at Kafka saw: the topic of captured payments and how far the Ledger is behind.</summary>
public sealed record KafkaState(int UnderReplicated, int MinIsr, IReadOnlyDictionary<int, int> LeadersByBroker, long? Lag);

/// <summary>The probes of the stand, all through kubectl: the tool needs no client library and no open ports.</summary>
public static partial class Cluster
{
    public const string Namespace = "highload";
    public const string Topic = "payments.captured";
    public const string LedgerGroup = "ledger";

    // Payments.Api/Database/AdvisoryLockKeys.cs. Below 2^32: in pg_locks the key is objid, with classid 0.
    public const long OutboxRelayLock = 7_301_001;
    public const long PaymentRecoveryLock = 7_301_002;

    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(8);

    public static async Task<Dictionary<string, bool>?> NodesReadyAsync()
    {
        var result = await Shell.Kubectl(ProbeTimeout, "get", "nodes", "-o", "json");
        if (!result.Ok)
            return null;

        using var json = JsonDocument.Parse(result.Output);
        var nodes = new Dictionary<string, bool>();
        foreach (var node in json.RootElement.GetProperty("items").EnumerateArray())
        {
            var ready = node.GetProperty("status").GetProperty("conditions").EnumerateArray()
                .Any(c => c.GetProperty("type").GetString() == "Ready" && c.GetProperty("status").GetString() == "True");
            nodes[node.GetProperty("metadata").GetProperty("name").GetString()!] = ready;
        }
        return nodes;
    }

    public static async Task<List<PodInfo>?> PodsAsync(string selector)
    {
        var result = await Shell.Kubectl(ProbeTimeout, "-n", Namespace, "get", "pods", "-l", selector, "-o", "json");
        if (!result.Ok)
            return null;

        using var json = JsonDocument.Parse(result.Output);
        var pods = new List<PodInfo>();
        foreach (var pod in json.RootElement.GetProperty("items").EnumerateArray())
        {
            var metadata = pod.GetProperty("metadata");
            if (metadata.TryGetProperty("deletionTimestamp", out _))
                continue;
            var status = pod.GetProperty("status");
            var ready = status.TryGetProperty("conditions", out var conditions) && conditions.EnumerateArray()
                .Any(c => c.GetProperty("type").GetString() == "Ready" && c.GetProperty("status").GetString() == "True");
            pods.Add(new PodInfo(
                metadata.GetProperty("name").GetString()!,
                pod.GetProperty("spec").TryGetProperty("nodeName", out var node) ? node.GetString()! : "",
                status.TryGetProperty("podIP", out var ip) ? ip.GetString() : null,
                ready));
        }
        return pods;
    }

    public static Task<List<PodInfo>?> PaymentsApiPodsAsync() => PodsAsync("app.kubernetes.io/name=payments-api");

    public static async Task<string?> PgPrimaryAsync()
    {
        var result = await Shell.Kubectl(ProbeTimeout, "-n", Namespace, "get", "cluster", "pg", "-o", "jsonpath={.status.currentPrimary}");
        return result.Ok && result.Output.Length > 0 ? result.Output : null;
    }

    /// <summary>A query on the primary, as the postgres user over the local socket: one line, fields split by |.</summary>
    public static async Task<string[]?> PsqlAsync(string primary, string sql, TimeSpan? timeout = null)
    {
        var result = await Shell.Kubectl(timeout ?? ProbeTimeout,
            "-n", Namespace, "exec", primary, "-c", "postgres", "--", "psql", "-d", "payments", "-AtF", "|", "-c", sql);
        return result.Ok ? result.Output.Split('|') : null;
    }

    public static async Task<PgState?> PgStateAsync(string primary)
    {
        // Who holds a session advisory lock: the client address of its connection, which is the IP of the pod.
        static string Holder(long key) => $"""
            coalesce((select host(a.client_addr) from pg_locks l join pg_stat_activity a on a.pid = l.pid
                      where l.locktype = 'advisory' and l.granted and l.classid = 0 and l.objid = {key} limit 1), '')
            """;
        var fields = await PsqlAsync(primary, $"""
            select count(*), coalesce(extract(epoch from now() - min(created_at))::int, 0),
                   {Holder(OutboxRelayLock)}, {Holder(PaymentRecoveryLock)}
            from outbox
            """);
        if (fields is not { Length: 4 })
            return null;
        return new PgState(
            long.Parse(fields[0], CultureInfo.InvariantCulture),
            int.Parse(fields[1], CultureInfo.InvariantCulture),
            fields[2].Length > 0 ? fields[2] : null,
            fields[3].Length > 0 ? fields[3] : null);
    }

    /// <summary>The time of the cluster, not of the laptop: the clock of WSL can drift from Windows.</summary>
    public static async Task<string?> PgNowAsync(string primary) =>
        (await PsqlAsync(primary, "select now()"))?[0];

    /// <summary>
    /// The topic and the lag of the Ledger, through the Kafka tools in a broker's pod. Each tool is a JVM that takes
    /// a few seconds to start, so Kafka is sampled by a loop of its own, as often as that allows.
    /// </summary>
    public static async Task<KafkaState?> KafkaStateAsync(string probePod)
    {
        var describe = await KafkaToolAsync(probePod, $"kafka-topics.sh --describe --topic {Topic}");
        if (!describe.Ok)
            return null;

        var underReplicated = 0;
        var minIsr = int.MaxValue;
        var leaders = new Dictionary<int, int>();
        foreach (Match partition in PartitionLine().Matches(describe.Output))
        {
            var leader = int.Parse(partition.Groups["leader"].Value, CultureInfo.InvariantCulture);
            var replicas = partition.Groups["replicas"].Value.Split(',').Length;
            var isr = partition.Groups["isr"].Value.Split(',', StringSplitOptions.RemoveEmptyEntries).Length;
            leaders[leader] = leaders.GetValueOrDefault(leader) + 1;
            if (isr < replicas)
                underReplicated++;
            minIsr = Math.Min(minIsr, isr);
        }
        if (minIsr == int.MaxValue)
            return null;

        var groups = await KafkaToolAsync(probePod, $"kafka-consumer-groups.sh --describe --group {LedgerGroup}");
        long? lag = null;
        if (groups.Ok)
        {
            lag = 0;
            foreach (Match row in LagLine().Matches(groups.Output))
                lag += long.Parse(row.Groups["lag"].Value, CultureInfo.InvariantCulture);
        }
        return new KafkaState(underReplicated, minIsr, leaders, lag);
    }

    // The admin client of the Kafka tools waits 30 s for a broker by default, and a broker on a stopped node never
    // answers: every probe during the outage would time out. Short timeouts make it give up on that broker and ask
    // another. The file goes to /tmp of the probe pod, an emptyDir.
    private const string ProbeConfig =
        "request.timeout.ms=5000\ndefault.api.timeout.ms=10000\nsocket.connection.setup.timeout.ms=2000\nsocket.connection.setup.timeout.max.ms=3000\n";

    // In a pod of its own (KafkaProbe), never in a broker's: each tool is a JVM, and in a broker's container it
    // shares the broker's memory limit. The first run of the tool ran them in broker 2, and broker 2 died as one
    // of them finished.
    private static Task<CommandResult> KafkaToolAsync(string probePod, string tool) =>
        Shell.Kubectl(TimeSpan.FromSeconds(30), "-n", Namespace, "exec", probePod, "--", "bash", "-c",
            $"printf '{ProbeConfig}' > /tmp/failover-probe.properties && KAFKA_HEAP_OPTS=-Xmx128m /opt/kafka/bin/{tool} " +
            "--bootstrap-server kafka-kafka-bootstrap:9092 --command-config /tmp/failover-probe.properties");

    /// <summary>The Ledger's /invariants: every payment balanced, every account matching its postings.</summary>
    public static async Task<(bool Ok, string Text)> InvariantsAsync()
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        try
        {
            var text = await http.GetStringAsync("http://localhost:8080/invariants");
            using var json = JsonDocument.Parse(text);
            return (json.RootElement.TryGetProperty("ok", out var ok) && ok.GetBoolean(), text);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException)
        {
            return (false, e.Message);
        }
    }

    // Topic: payments.captured  Partition: 0  Leader: 1  Replicas: 1,2,0  Isr: 2,0 ...
    [GeneratedRegex(@"Partition:\s*\d+\s+Leader:\s*(?<leader>-?\d+)\s+Replicas:\s*(?<replicas>[\d,]+)\s+Isr:\s*(?<isr>[\d,]*)")]
    private static partial Regex PartitionLine();

    // ledger  payments.captured  0  1155  1155  0  rdkafka-...
    [GeneratedRegex(@"^\S+\s+payments\.captured\s+\d+\s+\S+\s+\S+\s+(?<lag>\d+)", RegexOptions.Multiline)]
    private static partial Regex LagLine();
}
