using Payments.Api.Payments;
using System.Net;

namespace Payments.Api.Acquiring;

public sealed class HttpAcquirer(HttpClient http) : IAcquirer
{
    private const string ApprovedStatus = "approved";
    private const string DeclinedStatus = "declined";
    private const string AuthorizationsEndpoint = "v1/authorizations";

    public async Task<AcquirerDecision> AuthorizeAsync(Payment payment, CancellationToken cancellationToken)
    {
        var authRequest = new BankAuthorizationRequest(payment.Id, payment.MerchantId, payment.Amount, payment.Currency);
        using var response = await http.PostAsJsonAsync(AuthorizationsEndpoint, authRequest, cancellationToken);

        if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Conflict)
        {
            var msg = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException($"Response Code = {response.StatusCode}, paymentId = {payment.Id}, message = {msg} ");
        }

        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<BankAuthorizationResponse>(cancellationToken);

        if (body is null)
            throw new InvalidOperationException($"Contract violation, paymentId = {payment.Id}");

        return CreateAcquirerDecision(payment.Id, body);
    }

    public async Task<AcquirerDecision?> FindDecisionAsync(Guid paymentId, CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync($"{AuthorizationsEndpoint}/{paymentId}", cancellationToken);

        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;

        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<BankAuthorizationResponse>(cancellationToken);

        if (body is null)
            throw new InvalidOperationException($"Contract violation, paymentId = {paymentId}");

        return CreateAcquirerDecision(paymentId, body);
    }

    private static AcquirerDecision CreateAcquirerDecision(Guid paymentId, BankAuthorizationResponse body)
    {
        return body.Status switch
        {
            ApprovedStatus => new AcquirerDecision(true),
            DeclinedStatus => new AcquirerDecision(false),
            _ => throw new InvalidOperationException($"Unknown bank status '{body.Status}' for payment {paymentId}"),
        };
    }
}

internal sealed record BankAuthorizationRequest(Guid PaymentId, long MerchantId, long Amount, string Currency);
internal sealed record BankAuthorizationResponse(Guid PaymentId, string Status, string? AuthorizationCode, DateTimeOffset DecidedAt);
