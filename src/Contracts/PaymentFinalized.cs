namespace Contracts;

/// <summary>
/// A payment has reached its final status: captured, declined or voided (stage 5). Published by Payments.Api through
/// the outbox, written by the same statement that sets the status, so there is one event per payment, and only if the
/// transition really happened. For the history of a merchant's payments and, in stage 6, the webhooks; the ledger
/// keeps reading <see cref="PaymentCaptured"/>, so a captured payment has both events.
/// Amounts are in minor units (kopecks). Fee is null unless the payment was captured: no money moved otherwise.
/// </summary>
/// <param name="Status">One of <see cref="PaymentFinalStatus"/>: the status as the database spells it.</param>
/// <param name="FinalizedAt">When Payments.Api wrote the final status, on its clock.</param>
public sealed record PaymentFinalized(
    Guid PaymentId,
    long MerchantId,
    long Amount,
    long? Fee,
    string Currency,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset FinalizedAt);

/// <summary>The final statuses of a payment, as they travel in <see cref="PaymentFinalized"/>.</summary>
public static class PaymentFinalStatus
{
    public const string Captured = "captured";
    public const string Declined = "declined";
    public const string Voided = "voided";
}
