using System.Text.Json;
using System.Text.Json.Nodes;

namespace SagaDrill;

/// <summary>The Argo CD application of the stand: what it has synced, and whether it puts back changes made by hand.</summary>
public sealed record ArgoApp(string Revision, string Sync, string Health, bool SelfHeal);

/// <summary>A Deployment: how many replicas it should have and how many are ready.</summary>
public sealed record Replicas(int Desired, int Ready);

/// <summary>The probes of the stand, all through kubectl, as in load/Failover.</summary>
public static class Stand
{
    public const string Namespace = "highload";
    public const string Application = "highload";

    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(15);

    /// <summary>The application deploy/argocd/highload.yaml; the error of kubectl if it cannot be read.</summary>
    public static async Task<(ArgoApp? App, string Error)> ArgoAppAsync()
    {
        var result = await Shell.Kubectl(ProbeTimeout, "-n", "argocd", "get", "application", Application, "-o", "json");
        if (!result.Ok)
            return (null, result.Errors);

        using var json = JsonDocument.Parse(result.Output);
        var root = json.RootElement;
        // selfHeal is what the drill will switch off to stop the antifraud by hand, and what it must find on.
        var selfHeal = root.GetProperty("spec").TryGetProperty("syncPolicy", out var policy)
            && policy.TryGetProperty("automated", out var automated) && automated.ValueKind == JsonValueKind.Object
            && automated.TryGetProperty("selfHeal", out var heal) && heal.ValueKind == JsonValueKind.True;
        var status = root.GetProperty("status");
        return (new ArgoApp(
            Text(status, "sync", "revision"), Text(status, "sync", "status"), Text(status, "health", "status"), selfHeal), "");
    }

    public static async Task<(Replicas? Replicas, string Error)> DeploymentAsync(string name)
    {
        var result = await Shell.Kubectl(ProbeTimeout, "-n", Namespace, "get", "deployment", name, "-o", "json");

        if (!result.Ok)
            return (null, result.Errors);

        using var json = JsonDocument.Parse(result.Output);
        var root = json.RootElement;

        // A Deployment with no ready replica has no readyReplicas at all.
        var ready = root.GetProperty("status").TryGetProperty("readyReplicas", out var count) ? count.GetInt32() : 0;
        return (new Replicas(root.GetProperty("spec").GetProperty("replicas").GetInt32(), ready), "");
    }

    /// <summary>
    /// spec.syncPolicy.automated of the application in compact JSON, to put back as it was by
    /// <see cref="SetAutoSyncAsync"/>; null if auto-sync is off.
    /// </summary>
    public static async Task<string?> AutoSyncAsync()
    {
        var result = await Shell.Kubectl(ProbeTimeout, "-n", "argocd", "get", "application", Application, "-o", "json");

        if (!result.Ok)
            throw new DrillException($"Cannot read the Argo CD application {Application}: {result.Errors}");

        return JsonNode.Parse(result.Output)?["spec"]?["syncPolicy"]?["automated"]?.ToJsonString();
    }

    /// <summary>Auto-sync of the application off (null), or back on as <see cref="AutoSyncAsync"/> read it.</summary>
    public static async Task SetAutoSyncAsync(string? automated)
    {
        var patch = """{"spec":{"syncPolicy":{"automated":""" + (automated ?? "null") + "}}}";
        var result = await Shell.Kubectl(ProbeTimeout, "-n", "argocd", "patch", "application", Application, "--type", "merge", "-p", patch);

        if (!result.Ok)
            throw new DrillException($"Cannot {(automated is null ? "switch off" : "switch back on")} the auto-sync of the Argo CD " +
                $"application {Application}: {result.Errors}");
    }

    public static async Task ScaleAsync(string deployment, int replicas, string kind = "deployment")
    {
        var result = await Shell.Kubectl(ProbeTimeout, "-n", Namespace, "scale", $"{kind}/{deployment}", $"--replicas={replicas}");

        if (!result.Ok)
            throw new DrillException($"kubectl scale {kind}/{deployment} --replicas={replicas} failed: {result.Errors}");
    }

    /// <summary>A StatefulSet as <see cref="DeploymentAsync"/> reads a Deployment.</summary>
    public static async Task<(Replicas? Replicas, string Error)> StatefulSetAsync(string name)
    {
        var result = await Shell.Kubectl(ProbeTimeout, "-n", Namespace, "get", "statefulset", name, "-o", "json");

        if (!result.Ok)
            return (null, result.Errors);

        using var json = JsonDocument.Parse(result.Output);
        var root = json.RootElement;
        var ready = root.GetProperty("status").TryGetProperty("readyReplicas", out var count) ? count.GetInt32() : 0;

        return (new Replicas(root.GetProperty("spec").GetProperty("replicas").GetInt32(), ready), "");
    }

    /// <summary>
    /// An environment variable of a Deployment set (NAME=value) or removed (NAME-), as kubectl set env takes it. Its
    /// pods are replaced: <see cref="RolloutAsync"/> waits for them.
    /// </summary>
    public static async Task SetEnvAsync(string deployment, string variable)
    {
        var result = await Shell.Kubectl(ProbeTimeout, "-n", Namespace, "set", "env", $"deployment/{deployment}", variable);

        if (!result.Ok)
            throw new DrillException($"kubectl set env deployment/{deployment} {variable} failed: {result.Errors}");
    }

    /// <summary>The value of an environment variable in the pod template of a Deployment; null if it has none.</summary>
    public static async Task<string?> EnvAsync(string deployment, string variable)
    {
        var result = await Shell.Kubectl(ProbeTimeout, "-n", Namespace, "get", "deployment", deployment, "-o", "json");

        if (!result.Ok)
            throw new DrillException($"Cannot read deployment/{deployment}: {result.Errors}");

        var containers = JsonNode.Parse(result.Output)?["spec"]?["template"]?["spec"]?["containers"]?.AsArray() ?? [];

        return containers
            .SelectMany(c => c?["env"]?.AsArray() ?? [])
            .FirstOrDefault(e => e?["name"]?.GetValue<string>() == variable)?["value"]?.GetValue<string>();
    }

    /// <summary>Waits until the rollout of a Deployment is over: every pod of the new template ready.</summary>
    public static async Task RolloutAsync(string deployment, TimeSpan limit)
    {
        var result = await Shell.Kubectl(limit + ProbeTimeout,
            "-n", Namespace, "rollout", "status", $"deployment/{deployment}", $"--timeout={(int)limit.TotalSeconds}s");

        if (!result.Ok)
            throw new DrillException($"The rollout of deployment/{deployment} is not over in {limit.TotalSeconds:0} s: {result.Errors}");
    }

    /// <summary>
    /// Every pod of the label at once, without the grace period: the process gets SIGKILL and finishes nothing, as on a
    /// node that lost power. A delete, not an eviction: the PodDisruptionBudget does not hold it back.
    /// </summary>
    public static async Task KillPodsAsync(string name)
    {
        var result = await Shell.Kubectl(ProbeTimeout, "-n", Namespace, "delete", "pod", "-l", $"app.kubernetes.io/name={name}",
            "--grace-period=0", "--force", "--wait=false");

        if (!result.Ok)
            throw new DrillException($"kubectl delete pod -l app.kubernetes.io/name={name} failed: {result.Errors}");
    }

    private static string Text(JsonElement status, string section, string field) =>
        status.TryGetProperty(section, out var part) && part.TryGetProperty(field, out var value) ? value.GetString() ?? "" : "";
}
