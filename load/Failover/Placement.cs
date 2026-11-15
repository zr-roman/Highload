using System.Globalization;

namespace Failover;

/// <summary>Where things stood when the node went down, for the report.</summary>
public sealed record PlacementInfo(
    string PgPrimary, string PgPrimaryNode, string OutboxLeaderPod, int BrokerOnNode,
    int PartitionsLedOnNode, IReadOnlyDictionary<string, int> BrokerRestarts, IReadOnlyList<string> Moves);

/// <summary>
/// Puts on the node what the scenario needs there, and the rest elsewhere, then checks the stand is healthy.
/// Each move is the gentle kind: a switchover, a pod deleted with its connections closed, a cordon for a moment.
/// </summary>
public static class Placement
{
    private const string ServerNode = "k3d-highload-server-0";

    // One replica each: namespace, Deployment, the label of its pods.
    private static readonly (string Namespace, string Deployment, string Selector)[] Singletons =
    [
        ("kube-system", "traefik", "app.kubernetes.io/name=traefik"),
        ("kube-system", "metrics-server", "k8s-app=metrics-server"),
        (Cluster.Namespace, "ledger-service", "app.kubernetes.io/name=ledger-service"),
        (Cluster.Namespace, "bank-emulator", "app.kubernetes.io/name=bank-emulator"),
        (Cluster.Namespace, "antifraud", "app.kubernetes.io/name=antifraud"),
        (Cluster.Namespace, "redis", "app.kubernetes.io/name=redis"),
        (Cluster.Namespace, "otel-collector", "app.kubernetes.io/name=otel-collector"),
    ];

    public static async Task<PlacementInfo> PrepareAsync(FailoverOptions options, CancellationToken ct)
    {
        var node = options.NodeName;
        var moves = new List<string>();

        var nodes = await Cluster.NodesReadyAsync() ?? throw new FailoverException("kubectl get nodes failed: is the cluster up?");
        if (!nodes.ContainsKey(node))
            throw new FailoverException($"No node {node} in the cluster");
        if (nodes.Where(n => !n.Value).Select(n => n.Key).ToList() is { Count: > 0 } notReady)
            throw new FailoverException($"Nodes not Ready: {string.Join(", ", notReady)}. The run needs a healthy cluster");

        // 0. The services with a single replica off the node: otherwise the failure of the node is the failure of the
        // only copy of something every request needs, Traefik above all, and drowns what the scenario is about
        // (docs/observability.md). Each is deleted with the node cordoned, so its replacement starts elsewhere.
        foreach (var (ns, deployment, selector) in Singletons)
        {
            var copies = await Shell.Kubectl("-n", ns, "get", "pods", "-l", selector, "-o", "jsonpath={range .items[*]}{.metadata.name} {.spec.nodeName}{\"\\n\"}{end}");
            foreach (var line in copies.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var (pod, podNode) = (line.Split(' ')[0], line.Split(' ').ElementAtOrDefault(1));
                if (podNode != node)
                    continue;
                Console.WriteLine($"Moving {pod} off {node}");
                await WithCordonAsync([node], async () =>
                {
                    await Shell.KubectlOrThrow("-n", ns, "delete", "pod", pod);
                    await Shell.KubectlOrThrow("-n", ns, "rollout", "status", $"deployment/{deployment}", "--timeout=180s");
                });
                moves.Add($"`{pod}` (единственная копия {deployment}) перенесён с `{node}`");
            }
        }

        // 1. The primary of PostgreSQL away from the node: this scenario is about Kafka, not about a failover of the base.
        var primary = await Cluster.PgPrimaryAsync() ?? throw new FailoverException("No primary of the cluster pg");
        var instances = await Cluster.PodsAsync("cnpg.io/cluster=pg") ?? throw new FailoverException("No pods of the cluster pg");
        if (instances.First(p => p.Name == primary).Node == node)
        {
            var target = instances.FirstOrDefault(p => p.Node == ServerNode) ?? instances.First(p => p.Node != node);
            Console.WriteLine($"Switching the primary of PostgreSQL over from {primary} to {target.Name} ({target.Node})");
            await SwitchoverAsync(target.Name, ct);
            moves.Add($"основной PostgreSQL переключён с `{primary}` на `{target.Name}`");
            primary = target.Name;
        }
        await WaitAsync("the cluster pg to be healthy", TimeSpan.FromMinutes(3), ct, async () =>
            (await Shell.Kubectl("-n", Cluster.Namespace, "get", "cluster", "pg", "-o", "jsonpath={.status.phase}")).Output
            == "Cluster in healthy state");
        var primaryNode = instances.First(p => p.Name == primary).Node;

        // 2. A replica of Payments.Api on the node. The other agent is cordoned for a moment, so the replacement of a
        // deleted replica starts on this node or on the server, and the spread of payments-api.yaml prefers the node
        // that has none. Never the server node: a cordon of the primary's node makes CloudNativePG switch over
        // (docs/postgres-failover.md, finding 1).
        var pods = await Cluster.PaymentsApiPodsAsync() ?? throw new FailoverException("No pods of Payments.Api");
        for (var attempt = 1; !pods.Any(p => p.Node == node); attempt++)
        {
            if (attempt > 3)
                throw new FailoverException($"No replica of Payments.Api would start on {node}");
            var other = pods.FirstOrDefault(p => p.Node != ServerNode) ?? pods.First();
            Console.WriteLine($"Moving {other.Name} from {other.Node} towards {node}");
            await WithCordonAsync(nodes.Keys.Where(n => n != node && n != ServerNode), () => DeletePodAndWaitAsync(other.Name));
            moves.Add($"реплика Payments.Api `{other.Name}` удалена, чтобы замена встала на `{node}`");
            pods = await Cluster.PaymentsApiPodsAsync() ?? pods;
        }

        // 3. The leader of the outbox relay on the node. Another replica's lock is freed by deleting that replica: it
        // stops gracefully and closes its connections, and a waiting replica takes the lock within 5 s (RetryInterval
        // of AdvisoryLeaderLock). The node itself is cordoned meanwhile, so the replacement starts elsewhere.
        var leader = "";
        for (var attempt = 1; ; attempt++)
        {
            primary = await Cluster.PgPrimaryAsync() ?? primary;
            pods = await Cluster.PaymentsApiPodsAsync() ?? pods;
            var holder = await LeaderPodAsync(primary, pods, ct);
            if (holder?.Node == node)
            {
                leader = holder.Name;
                break;
            }
            if (attempt > 4)
                throw new FailoverException($"The leader of the outbox relay did not move to {node}");
            if (holder is null)
            {
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
                continue;
            }
            Console.WriteLine($"Moving the leadership of the outbox relay away from {holder.Name} ({holder.Node})");
            await WithCordonAsync([node], () => DeletePodAndWaitAsync(holder.Name));
            moves.Add($"лидерство outbox снято с `{holder.Name}` удалением пода");
        }

        // 4. Kafka: the broker on the node, and how many times each broker has restarted so far.
        var brokers = await Cluster.PodsAsync("strimzi.io/cluster=kafka,strimzi.io/pool-name=broker")
            ?? throw new FailoverException("No brokers of the Kafka cluster kafka");
        var onNode = brokers.FirstOrDefault(b => b.Node == node) ?? throw new FailoverException($"No Kafka broker on {node}");
        var brokerId = int.Parse(onNode.Name[(onNode.Name.LastIndexOf('-') + 1)..], CultureInfo.InvariantCulture);
        var restarts = await BrokerRestartsAsync();

        var kafka = await Cluster.KafkaStateAsync(KafkaProbe.Name) ?? throw new FailoverException("The Kafka tools failed in the pod " + KafkaProbe.Name);
        if (kafka.UnderReplicated > 0)
            throw new FailoverException($"{kafka.UnderReplicated} partitions of {Cluster.Topic} are under-replicated: wait for the brokers to catch up");
        var led = kafka.LeadersByBroker.GetValueOrDefault(brokerId);
        if (led == 0)
            Console.WriteLine($"Broker {brokerId} on {node} leads no partition: no leader election to see, only the ISR shrinking");

        // 5. Nothing in flight: the outbox close to empty, the Ledger caught up.
        var pg = await Cluster.PgStateAsync(primary) ?? throw new FailoverException("Cannot read the outbox");
        if (pg.Outbox > 100)
            throw new FailoverException($"{pg.Outbox} rows in the outbox: another load is running, or the relay is stuck");
        if (kafka.Lag > 100)
            throw new FailoverException($"The Ledger is {kafka.Lag} messages behind: another load is running, or the Ledger is stuck");

        return new PlacementInfo(primary, primaryNode, leader, brokerId, led, restarts, moves);
    }

    /// <summary>Restarts of the container of each broker: one more after the run means a broker fell over.</summary>
    public static async Task<Dictionary<string, int>> BrokerRestartsAsync()
    {
        var output = await Shell.KubectlOrThrow("-n", Cluster.Namespace, "get", "pods", "-l", "strimzi.io/cluster=kafka,strimzi.io/pool-name=broker",
            "-o", "jsonpath={range .items[*]}{.metadata.name} {.status.containerStatuses[0].restartCount}{\"\\n\"}{end}");
        return output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Split(' '))
            .ToDictionary(f => f[0], f => int.Parse(f[1], CultureInfo.InvariantCulture));
    }

    /// <summary>The replica of Payments.Api that holds the lock of the outbox relay.</summary>
    public static async Task<PodInfo?> LeaderPodAsync(string primary, IReadOnlyList<PodInfo> pods, CancellationToken ct)
    {
        var state = await Cluster.PgStateAsync(primary);
        return state?.OutboxLeaderIp is { } ip ? pods.FirstOrDefault(p => p.Ip == ip) : null;
    }

    // The switchover kubectl cnpg promote does: a new targetPrimary in the status (docs/postgres-failover.md).
    private static async Task SwitchoverAsync(string target, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        var patch = $$$"""{"status":{"targetPrimary":"{{{target}}}","targetPrimaryTimestamp":"{{{now}}}","phase":"Switchover in progress","phaseReason":"Switching over to {{{target}}}"}}""";
        await Shell.KubectlOrThrow("-n", Cluster.Namespace, "patch", "cluster", "pg", "--subresource=status", "--type=merge", "-p", patch);
        await WaitAsync($"{target} to become the primary", TimeSpan.FromMinutes(2), ct, async () => await Cluster.PgPrimaryAsync() == target);
    }

    private static async Task DeletePodAndWaitAsync(string pod)
    {
        await Shell.KubectlOrThrow("-n", Cluster.Namespace, "delete", "pod", pod);
        await Shell.KubectlOrThrow("-n", Cluster.Namespace, "rollout", "status", "deployment/payments-api", "--timeout=120s");
    }

    // Cordon touches no running pod: it only keeps new ones off these nodes until the action is done.
    private static async Task WithCordonAsync(IEnumerable<string> nodes, Func<Task> action)
    {
        var cordoned = nodes.ToList();
        foreach (var n in cordoned)
            await Shell.KubectlOrThrow("cordon", n);
        try
        {
            await action();
        }
        finally
        {
            foreach (var n in cordoned)
                await Shell.Kubectl("uncordon", n);
        }
    }

    private static async Task WaitAsync(string what, TimeSpan limit, CancellationToken ct, Func<Task<bool>> condition)
    {
        var deadline = DateTimeOffset.Now + limit;
        while (!await condition())
        {
            if (DateTimeOffset.Now > deadline)
                throw new FailoverException($"Waited {limit.TotalSeconds:0} s for {what}");
            await Task.Delay(TimeSpan.FromSeconds(3), ct);
        }
    }
}
