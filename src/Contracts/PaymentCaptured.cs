namespace Contracts;

/// <summary>
/// Проводка:
/// Published by Payments.Api through the outbox after the acquirer approved the payment.
/// Amounts are in minor units (kopecks): money never goes through floating point.
/// For the ledger, and for the history of payments as well (stage 5): a captured payment has this event alone, the
/// declined and voided ones have <see cref="PaymentFinalized"/>. The statement that captures the payment builds it from
/// the row it has just written, so every field is what the database holds, to the microsecond.
/// </summary>
/// <param name="CapturedAt">When the payment was captured, by the clock of the database of Payments.Api.</param>
/// <param name="CreatedAt">When the payment was created, as its row says: the history orders a merchant's payments by it.</param>
public sealed record PaymentCaptured(
    Guid PaymentId,
    long MerchantId,
    long Amount,
    long Fee,
    string Currency,
    DateTimeOffset CapturedAt,
    DateTimeOffset CreatedAt);
