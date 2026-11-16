namespace Failover;

/// <summary>The scenarios: what is put on the node before it goes down, and so what the run checks.</summary>
public enum Scenario
{
    /// <summary>
    /// A Kafka broker that leads partitions, and the leader of the outbox relay of Payments.Api, on the node; the
    /// primary of PostgreSQL elsewhere. Checks that Kafka elects new leaders and keeps writing with two brokers, and
    /// that another replica takes over the outbox while the node is still down.
    /// </summary>
    KafkaBroker,
}

/// <summary>How the node goes away.</summary>
public enum FailureMode
{
    /// <summary>
    /// docker stop: the node's container is stopped. Its processes get a signal and exit, and their connections are
    /// closed properly: the peers see the close at once.
    /// </summary>
    Stop,

    /// <summary>
    /// docker network disconnect: the node drops out of the network with everything on it still running. Nothing is
    /// closed: the peers' connections to it just go silent, as when a machine loses power or its switch port.
    /// </summary>
    Partition,
}

/// <summary>The command line of the tool.</summary>
public sealed record FailoverOptions(
    Scenario Scenario, string Node, int Rate, int BeforeSeconds, int DownSeconds, int LoadSeconds, int SettleSeconds, FailureMode Mode)
{
    public const string Usage = """
        A node of the k3d cluster fails under load: prepares the stand for the scenario, stops the node, samples the
        stand every 2 s, checks that nothing was lost and writes load/reports/failover-<time>/report.md.

        From the repository root, with the cluster up and no other load:
          dotnet run --project load\Failover -c Release
          dotnet run --project load\Failover -c Release -- --scenario kafka-broker --node agent-0 --rate 50
          dotnet run --project load\Failover -c Release -- --mode partition

          --scenario S  what to put on the node (default: kafka-broker)
                          kafka-broker  a Kafka broker that leads partitions and the leader of the outbox relay;
                                        the primary of PostgreSQL is moved away from the node
          --mode M      how the node goes away (default: stop)
                          stop       docker stop: its processes exit and close their connections
                          partition  docker network disconnect: it stays up but drops out of the network;
                                     nothing is closed, the peers' connections go silent
          --node N      the agent node to stop: agent-0 or agent-1 (default: agent-1). Not the server node:
                        the operators and the control plane live there
          --rate R      payments a second (default: 50)
          --before S    seconds of load before the node is stopped (default: 30)
          --down S      seconds the node stays down (default: 90)
          --load S      seconds of load in all (default: 150)
          --settle S    after the load, how long to wait for the stand to recover before the checks (default: 180)
        """;

    public static FailoverOptions Parse(string[] args)
    {
        var options = new FailoverOptions(Scenario.KafkaBroker, "agent-1", 50, 30, 90, 150, 180, FailureMode.Stop);
        for (var i = 0; i < args.Length; i++)
        {
            var name = args[i];
            if (i + 1 >= args.Length)
                throw new FailoverException($"{name} needs a value. {Usage}");
            var value = args[++i];
            options = name switch
            {
                "--scenario" => options with { Scenario = ParseScenario(value) },
                "--node" => options with { Node = value },
                "--mode" => options with { Mode = value switch
                {
                    "stop" => FailureMode.Stop,
                    "partition" => FailureMode.Partition,
                    _ => throw new FailoverException($"Unknown mode {value}: stop or partition"),
                } },
                "--rate" => options with { Rate = Positive(name, value) },
                "--before" => options with { BeforeSeconds = Positive(name, value) },
                "--down" => options with { DownSeconds = Positive(name, value) },
                "--load" => options with { LoadSeconds = Positive(name, value) },
                "--settle" => options with { SettleSeconds = Positive(name, value) },
                _ => throw new FailoverException($"Unknown option {name}. {Usage}"),
            };
        }

        if (options.Node is not ("agent-0" or "agent-1"))
            throw new FailoverException($"--node must be agent-0 or agent-1, got {options.Node}: the server node keeps the operators");
        if (options.BeforeSeconds + options.DownSeconds >= options.LoadSeconds)
            throw new FailoverException("--before + --down must be shorter than --load: the node comes back under load");
        return options;
    }

    /// <summary>The name of the node's Docker container and of the Kubernetes node.</summary>
    public string NodeName => $"k3d-highload-{Node}";

    public static string ScenarioName(Scenario scenario) => scenario switch
    {
        Scenario.KafkaBroker => "kafka-broker",
        _ => scenario.ToString(),
    };

    public static string ModeName(FailureMode mode) => mode == FailureMode.Partition ? "partition" : "stop";

    private static Scenario ParseScenario(string value) => value switch
    {
        "kafka-broker" => Scenario.KafkaBroker,
        _ => throw new FailoverException($"Unknown scenario {value}: kafka-broker"),
    };

    private static int Positive(string name, string value) =>
        int.TryParse(value, out var number) && number > 0 ? number : throw new FailoverException($"{name} must be a positive number, got {value}");
}
