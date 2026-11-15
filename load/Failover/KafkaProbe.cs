using System.Text.Json;

namespace Failover;

/// <summary>
/// A pod for the Kafka tools, from the image of the brokers, on the server node, deleted at the end of the run.
/// Its label app.kubernetes.io/name=kafka-probe is let in to the listener by the NetworkPolicy of Strimzi
/// (networkPolicyPeers in deploy/k8s/kafka.yaml).
/// </summary>
public sealed class KafkaProbe : IAsyncDisposable
{
    public const string Name = "kafka-probe";

    public static async Task<KafkaProbe> StartAsync(CancellationToken ct)
    {
        await Shell.Kubectl("-n", Cluster.Namespace, "delete", "pod", Name, "--ignore-not-found", "--wait=true");
        var image = await Shell.KubectlOrThrow("-n", Cluster.Namespace, "get", "pods", "-l", "strimzi.io/cluster=kafka,strimzi.io/pool-name=broker",
            "-o", "jsonpath={.items[0].spec.containers[0].image}");

        // Restricted, as the namespace demands (namespace.yaml); /tmp for the properties file.
        var overrides = JsonSerializer.Serialize(new
        {
            spec = new
            {
                nodeSelector = new Dictionary<string, string> { ["node-role.kubernetes.io/control-plane"] = "true" },
                securityContext = new { runAsNonRoot = true, runAsUser = 1001, seccompProfile = new { type = "RuntimeDefault" } },
                containers = new[]
                {
                    new
                    {
                        name = Name,
                        image,
                        command = new[] { "sleep", "infinity" },
                        securityContext = new
                        {
                            allowPrivilegeEscalation = false,
                            readOnlyRootFilesystem = true,
                            capabilities = new { drop = new[] { "ALL" } },
                        },
                        resources = new
                        {
                            requests = new Dictionary<string, string> { ["cpu"] = "50m", ["memory"] = "192Mi" },
                            limits = new Dictionary<string, string> { ["memory"] = "384Mi" },
                        },
                        volumeMounts = new[] { new { name = "tmp", mountPath = "/tmp" } },
                    },
                },
                volumes = new[] { new { name = "tmp", emptyDir = new { } } },
            },
        });
        await Shell.KubectlOrThrow("-n", Cluster.Namespace, "run", Name, $"--image={image}", "--restart=Never",
            $"--labels=app.kubernetes.io/name={Name}", $"--overrides={overrides}");
        await Shell.KubectlOrThrow("-n", Cluster.Namespace, "wait", $"pod/{Name}", "--for=condition=Ready", "--timeout=180s");
        return new KafkaProbe();
    }

    public async ValueTask DisposeAsync() =>
        await Shell.Kubectl("-n", Cluster.Namespace, "delete", "pod", Name, "--ignore-not-found", "--wait=false");
}
