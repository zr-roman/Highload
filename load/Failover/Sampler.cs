using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Failover;

/// <summary>One look at the stand, every 2 s. Null: that probe got no answer this time.</summary>
public sealed record Sample(
    TimeSpan At, bool? NodeReady, string? PgPrimary, long? Outbox, int? OutboxAgeSeconds,
    string? OutboxLeader, string? RecoveryLeader, int ApiReady, int ApiPods);

/// <summary>One look at Kafka, as often as its tools allow (a few seconds each).</summary>
public sealed record KafkaSample(TimeSpan At, int? UnderReplicated, int? MinIsr, int? LedOnNode, long? Lag);

/// <summary>
/// Samples the stand from the start of the load until the checks: two loops, one for Kubernetes and PostgreSQL every
/// 2 s, one for Kafka. Times are from the start of the load. Prints a line per sample, so the run can be watched.
/// </summary>
public sealed class Sampler(FailoverOptions options, PlacementInfo placement, Stopwatch clock)
{
    private readonly List<Sample> samples = [];
    private readonly List<KafkaSample> kafkaSamples = [];
    private string primary = placement.PgPrimary;

    public IReadOnlyList<Sample> Samples { get { lock (samples) return samples.ToList(); } }

    public IReadOnlyList<KafkaSample> KafkaSamples { get { lock (kafkaSamples) return kafkaSamples.ToList(); } }

    public Sample? Last { get { lock (samples) return samples.LastOrDefault(); } }

    public KafkaSample? LastKafka { get { lock (kafkaSamples) return kafkaSamples.LastOrDefault(); } }

    public Task RunAsync(CancellationToken ct) => Task.WhenAll(StandLoopAsync(ct), KafkaLoopAsync(ct));

    private async Task StandLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var started = clock.Elapsed;
            var nodes = Cluster.NodesReadyAsync();
            var pods = Cluster.PaymentsApiPodsAsync();
            var currentPrimary = Cluster.PgPrimaryAsync();
            await Task.WhenAll(nodes, pods, currentPrimary);
            primary = currentPrimary.Result ?? primary;
            var pg = await Cluster.PgStateAsync(primary);

            string? Pod(string? ip) =>
                ip is null ? null : pods.Result?.FirstOrDefault(p => p.Ip == ip)?.Name ?? ip;

            var sample = new Sample(
                started,
                nodes.Result is { } n && n.TryGetValue(options.NodeName, out var ready) ? ready : null,
                currentPrimary.Result,
                pg?.Outbox,
                pg?.OutboxAgeSeconds,
                Pod(pg?.OutboxLeaderIp),
                Pod(pg?.RecoveryLeaderIp),
                pods.Result?.Count(p => p.Ready) ?? 0,
                pods.Result?.Count ?? 0);
            lock (samples)
                samples.Add(sample);
            Console.WriteLine(Line(sample, LastKafka));

            var wait = TimeSpan.FromSeconds(2) - (clock.Elapsed - started);
            if (wait > TimeSpan.Zero)
                await Delay(wait, ct);
        }
    }

    private async Task KafkaLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var started = clock.Elapsed;
            var state = await Cluster.KafkaStateAsync(KafkaProbe.Name);
            lock (kafkaSamples)
                kafkaSamples.Add(new KafkaSample(
                    started, state?.UnderReplicated, state?.MinIsr,
                    state?.LeadersByBroker.GetValueOrDefault(placement.BrokerOnNode), state?.Lag));
            if (state is null)
                await Delay(TimeSpan.FromSeconds(2), ct);
        }
    }

    private static async Task Delay(TimeSpan wait, CancellationToken ct)
    {
        try
        {
            await Task.Delay(wait, ct);
        }
        catch (OperationCanceledException)
        {
            // The sampling is over.
        }
    }

    private string Line(Sample s, KafkaSample? k)
    {
        var text = new StringBuilder($"{s.At.TotalSeconds,5:0} s  ");
        text.Append(s.NodeReady switch { true => "node Ready     ", false => "node NotReady  ", null => "node ?         " });
        text.Append($"pg {Short(s.PgPrimary)}  ");
        text.Append(s.Outbox is { } outbox ? $"outbox {outbox,5} ({s.OutboxAgeSeconds,3} s)  " : "outbox ?            ");
        text.Append($"relay {Short(s.OutboxLeader)}  api {s.ApiReady}/{s.ApiPods}");
        if (k is not null)
            text.Append($"  | kafka URP {Show(k.UnderReplicated)} ISR≥{Show(k.MinIsr)} led by {placement.BrokerOnNode}: {Show(k.LedOnNode)} lag {Show(k.Lag)}");
        return text.ToString();
    }

    private static string Show<T>(T? value) where T : struct => value?.ToString() ?? "?";

    // payments-api-f876f8df6-fw5t6 → fw5t6: enough to tell the replicas apart in a line.
    private static string Short(string? pod) => pod is null ? "—" : pod[(pod.LastIndexOf('-') + 1)..];

    public void WriteCsv(string path)
    {
        var csv = new StringBuilder("seconds,source,node_ready,pg_primary,outbox,outbox_age_s,relay_leader,recovery_leader,api_ready,api_pods,under_replicated,min_isr,led_on_node,lag\n");
        foreach (var s in Samples)
            csv.Append(CultureInfo.InvariantCulture,
                $"{s.At.TotalSeconds:0.0},stand,{s.NodeReady},{s.PgPrimary},{s.Outbox},{s.OutboxAgeSeconds},{s.OutboxLeader},{s.RecoveryLeader},{s.ApiReady},{s.ApiPods},,,,\n");
        foreach (var k in KafkaSamples)
            csv.Append(CultureInfo.InvariantCulture,
                $"{k.At.TotalSeconds:0.0},kafka,,,,,,,,,{k.UnderReplicated},{k.MinIsr},{k.LedOnNode},{k.Lag}\n");
        File.WriteAllText(path, csv.ToString());
    }
}
