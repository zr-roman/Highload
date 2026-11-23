using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace SagaDrill;

/// <summary>What the bank emulator says of a sample of payments, by its status of each: approved, declined, voided, or none.</summary>
public sealed record BankSample(int Asked, IReadOnlyDictionary<string, int> ByStatus)
{
    public int Voided => ByStatus.GetValueOrDefault("voided");
}

/// <summary>
/// The bank emulator of the stand, GET /v1/authorizations/{paymentId}, through a port-forward for the length of the
/// call: it is not behind Traefik.
/// </summary>
public static class Bank
{
    private const string Service = "svc/bank-emulator";
    private const int Port = 5300;

    /// <summary>The status the bank has for each payment, or "нет" for one it does not know (404) or did not answer.</summary>
    public static async Task<BankSample> StatusesAsync(IReadOnlyList<string> paymentIds, string reports, CancellationToken ct)
    {
        var port = FreePort();
        var log = Path.Combine(reports, "bank-port-forward.txt");
        using var portForward = new LongProcess("kubectl", ["-n", Stand.Namespace, "port-forward", Service, $"{port}:{Port}"], log);
        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/"), Timeout = TimeSpan.FromSeconds(10) };
        await WaitReadyAsync(http, portForward, log, ct);

        var byStatus = new Dictionary<string, int>();
        foreach (var id in paymentIds)
        {
            var status = await StatusAsync(http, id, ct);
            byStatus[status] = byStatus.GetValueOrDefault(status) + 1;
        }
        return new BankSample(paymentIds.Count, byStatus);
    }

    private static async Task<string> StatusAsync(HttpClient http, string paymentId, CancellationToken ct)
    {
        try
        {
            using var response = await http.GetAsync($"v1/authorizations/{paymentId}", ct);
            if (!response.IsSuccessStatusCode)
                return response.StatusCode == HttpStatusCode.NotFound ? "нет" : $"HTTP {(int)response.StatusCode}";
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            return json.RootElement.TryGetProperty("status", out var status) ? status.GetString() ?? "?" : "?";
        }
        catch (Exception e) when (e is HttpRequestException || (e is TaskCanceledException && !ct.IsCancellationRequested))
        {
            return "не ответил";
        }
    }

    private static async Task WaitReadyAsync(HttpClient http, LongProcess portForward, string log, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            if (portForward.HasExited)
                throw new DrillException($"kubectl port-forward to {Service} exited, see {log}");
            try
            {
                using var response = await http.GetAsync("healthz", ct);
                if (response.IsSuccessStatusCode)
                    return;
            }
            catch (Exception e) when (e is HttpRequestException || (e is TaskCanceledException && !ct.IsCancellationRequested))
            {
                // The port-forward has not opened its port yet.
            }
            await Task.Delay(TimeSpan.FromSeconds(1), ct);
        }
        throw new DrillException($"The bank emulator does not answer through the port-forward in 30 s, see {log}");
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
