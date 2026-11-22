using System.Text.Json;

namespace SagaDrill;

/// <summary>The API of Ledger.Service through Traefik, as a client sees it.</summary>
public static class Ledger
{
    private const string Address = "http://localhost:8080";

    /// <summary>/invariants: every payment balanced, every account matching its postings, the balances summing to zero.</summary>
    public static async Task<(bool Ok, string Text)> InvariantsAsync()
    {
        var text = await GetAsync("/invariants");
        if (text is null)
            return (false, "Ledger did not answer /invariants");
        using var json = JsonDocument.Parse(text);
        return (json.RootElement.TryGetProperty("ok", out var ok) && ok.GetBoolean(), text);
    }

    /// <summary>The balance of a merchant, the sum of its sub-accounts; null if the merchant has none or Ledger is silent.</summary>
    public static async Task<long?> BalanceAsync(long merchantId)
    {
        var text = await GetAsync($"/balances/merchants/{merchantId}");
        if (text is null)
            return null;
        using var json = JsonDocument.Parse(text);
        return json.RootElement.TryGetProperty("balance", out var balance) ? balance.GetInt64() : null;
    }

    private static async Task<string?> GetAsync(string path)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        try
        {
            using var response = await http.GetAsync(Address + path);
            return response.IsSuccessStatusCode ? await response.Content.ReadAsStringAsync() : null;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            return null;
        }
    }
}
