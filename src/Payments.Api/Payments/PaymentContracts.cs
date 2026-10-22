namespace Payments.Api.Payments;

/// <summary>Request body of POST /payments. Amount is in kopecks.</summary>
public sealed record CreatePaymentRequest(long MerchantId, long Amount, string? Currency);

public sealed record PaymentResponse(
    Guid Id,
    long MerchantId,
    long Amount,
    string Currency,
    long? Fee,
    string Status,
    DateTimeOffset CreatedAt)
{
    public static PaymentResponse From(Payment payment) => new(
        payment.Id,
        payment.MerchantId,
        payment.Amount,
        payment.Currency,
        payment.Fee,
        payment.Status.ToDb(),
        payment.CreatedAt);
}
