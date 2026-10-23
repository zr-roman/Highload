using Payments.Api.Payments;
using Polly;
using Polly.CircuitBreaker;
using System.Net;

namespace Payments.Api.Acquiring;

public sealed class HttpAcquirer(HttpClient http, ILogger<HttpAcquirer> logger) : IAcquirer
{
    private const string ApprovedStatus = "approved";
    private const string DeclinedStatus = "declined";
    private const string AuthorizationsEndpoint = "v1/authorizations";

    public async Task<AcquirerDecision> AuthorizeAsync(Payment payment, CancellationToken cancellationToken)
    {
        try
        {
            var authRequest = new BankAuthorizationRequest(payment.Id, payment.MerchantId, payment.Amount, payment.Currency);
            using var response = await http.PostAsJsonAsync(AuthorizationsEndpoint, authRequest, cancellationToken);

            if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Conflict)
            {
                var msg = await response.Content.ReadAsStringAsync(cancellationToken);
                throw new InvalidOperationException($"Response Code = {response.StatusCode}, paymentId = {payment.Id}, message = {msg} ");
            }

            if ((int)response.StatusCode >= (int)HttpStatusCode.InternalServerError)
            {
                logger.LogWarning("Bank answered {StatusCode} for payment {PaymentId}, outcome unknown", (int)response.StatusCode, payment.Id);
                return new AcquirerDecision(AcquirerOutcome.Unknown);
            }

            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"Bank answered {(int)response.StatusCode} to the authorization of payment {payment.Id}");

            var body = await response.Content.ReadFromJsonAsync<BankAuthorizationResponse>(cancellationToken);

            if (body is null)
                throw new InvalidOperationException($"Contract violation, paymentId = {payment.Id}");

            return CreateAcquirerDecision(payment.Id, body);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or ExecutionRejectedException && !cancellationToken.IsCancellationRequested)
        {
            // While the circuit is open every payment fails this way, and Polly logs the opening itself.
            var level = ex is BrokenCircuitException ? LogLevel.Debug : LogLevel.Warning;
            logger.Log(level, ex, "Bank call for payment {PaymentId} failed, outcome unknown", payment.Id);
            return new AcquirerDecision(AcquirerOutcome.Unknown);
        }
    }

    public async Task<AcquirerDecision?> FindDecisionAsync(Guid paymentId, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await http.GetAsync($"{AuthorizationsEndpoint}/{paymentId}", cancellationToken);

            if (response.StatusCode == HttpStatusCode.NotFound)
                return null;

            if ((int)response.StatusCode >= (int)HttpStatusCode.InternalServerError)
            {
                logger.LogWarning("Bank answered {StatusCode} to a decision lookup for payment {PaymentId}", (int)response.StatusCode, paymentId);
                return new AcquirerDecision(AcquirerOutcome.Unknown);
            }

            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"Bank answered {(int)response.StatusCode} to a decision lookup for payment {paymentId}");

            var body = await response.Content.ReadFromJsonAsync<BankAuthorizationResponse>(cancellationToken);

            if (body is null)
                throw new InvalidOperationException($"Contract violation, paymentId = {paymentId}");

            return CreateAcquirerDecision(paymentId, body);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or ExecutionRejectedException && !cancellationToken.IsCancellationRequested)
        {
            // While the circuit is open every payment fails this way, and Polly logs the opening itself.
            var level = ex is BrokenCircuitException ? LogLevel.Debug : LogLevel.Warning;
            logger.Log(level, ex, "Decision lookup for payment {PaymentId} failed, outcome unknown", paymentId);
            return new AcquirerDecision(AcquirerOutcome.Unknown);
        }
    }

    private static AcquirerDecision CreateAcquirerDecision(Guid paymentId, BankAuthorizationResponse body)
    {
        return body.Status switch
        {
            ApprovedStatus => new AcquirerDecision(AcquirerOutcome.Approved, body.AuthorizationCode),
            DeclinedStatus => new AcquirerDecision(AcquirerOutcome.Declined, body.AuthorizationCode),
            _ => throw new InvalidOperationException($"Unknown bank status '{body.Status}' for payment {paymentId}"),
        };
    }
}

internal sealed record BankAuthorizationRequest(Guid PaymentId, long MerchantId, long Amount, string Currency);
internal sealed record BankAuthorizationResponse(Guid PaymentId, string Status, string? AuthorizationCode, DateTimeOffset DecidedAt);
