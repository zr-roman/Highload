using System.Text.Json;

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

    private static string Text(JsonElement status, string section, string field) =>
        status.TryGetProperty(section, out var part) && part.TryGetProperty(field, out var value) ? value.GetString() ?? "" : "";
}
