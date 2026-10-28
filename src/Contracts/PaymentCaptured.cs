namespace Contracts;

/// <summary>
/// Проводка:
/// Published by Payments.Api through the outbox after the acquirer approved the payment.
/// Amounts are in minor units (kopecks): money never goes through floating point.
/// </summary>
public sealed record PaymentCaptured(
    Guid PaymentId,
    long MerchantId,
    long Amount,
    long Fee,
    string Currency,
    DateTimeOffset CapturedAt);
