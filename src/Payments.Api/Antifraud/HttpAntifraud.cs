using System.Net;
using System.Text.Json.Serialization;
using Payments.Api.Payments;
using Payments.Api.RateLimiting;
using Polly;
using Polly.CircuitBreaker;

namespace Payments.Api.Antifraud;

/// <summary>The client of the antifraud service (src/antifraud-service): POST /v1/checks.</summary>
public sealed class HttpAntifraud(HttpClient http, MerchantQuotas quotas, ILogger<HttpAntifraud> logger) : IAntifraud
{
    private const string ChecksEndpoint = "v1/checks";
    private const string AllowDecision = "allow";
    private const string DenyDecision = "deny";
    private const string LargeMerchantClass = "large";

    public async Task<AntifraudDecision> CheckAsync(Payment payment, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await http.PostAsJsonAsync(ChecksEndpoint, CheckRequestOf(payment), cancellationToken);

            // 400: our request broke the contract (antifraud validates it strictly). A retry would get the same, and
            // Unknown would hide a bug of Payments.Api behind fail open: it must be seen at once.
            if (response.StatusCode == HttpStatusCode.BadRequest)
            {
                var error = await response.Content.ReadAsStringAsync(cancellationToken);
                throw new InvalidOperationException($"Antifraud rejected the check of payment {payment.Id}: {error}");
            }

            // 503 is antifraud without Redis: no stored decision and no counter, so no answer to trust.
            if ((int)response.StatusCode >= (int)HttpStatusCode.InternalServerError)
            {
                logger.LogWarning("Antifraud answered {StatusCode} for payment {PaymentId}, outcome unknown", (int)response.StatusCode, payment.Id);
                return AntifraudDecision.Unknown;
            }

            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"Antifraud answered {(int)response.StatusCode} to the check of payment {payment.Id}");

            var body = await response.Content.ReadFromJsonAsync<CheckResponse>(cancellationToken)
                ?? throw new InvalidOperationException($"Contract violation: empty check of payment {payment.Id}");

            var outcome = body.Decision switch
            {
                AllowDecision => AntifraudOutcome.Allow,
                DenyDecision => AntifraudOutcome.Deny,
                _ => throw new InvalidOperationException($"Unknown antifraud decision '{body.Decision}' for payment {payment.Id}"),
            };

            return new AntifraudDecision(outcome, body.Score, body.Reasons ?? [], body.ModelVersion);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or ExecutionRejectedException && !cancellationToken.IsCancellationRequested)
        {
            // While the circuit is open every check fails this way, and Polly logs the opening itself.
            var level = ex is BrokenCircuitException ? LogLevel.Debug : LogLevel.Warning;
            logger.Log(level, ex, "Antifraud check of payment {PaymentId} failed, outcome unknown", payment.Id);
            return AntifraudDecision.Unknown;
        }
    }

    // A large merchant, one with a quota of its own, comes with its class and quota: the velocity rule takes its limit
    // from the quota, which lives here only. The standard one sends neither: antifraud rejects a quota without the
    // large class (400).
    private CheckRequest CheckRequestOf(Payment payment)
    {
        var quota = quotas.Of(payment.MerchantId);
        return new CheckRequest(
            payment.Id, payment.MerchantId, payment.Amount, payment.Currency, payment.CreatedAt,
            quota.IsOverride ? LargeMerchantClass : null,
            quota.IsOverride ? quota.TokensPerSecond : null);
    }
}

// createdAt goes with its offset (DateTimeOffset): antifraud counts the velocity window from it and rejects a time
// without an offset.
internal sealed record CheckRequest(
    Guid PaymentId,
    long MerchantId,
    long Amount,
    string Currency,
    DateTimeOffset CreatedAt,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? MerchantClass,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? QuotaPerSecond);

internal sealed record CheckResponse(Guid PaymentId, string Decision, double Score, string[]? Reasons, string? ModelVersion);
