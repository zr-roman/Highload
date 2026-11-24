namespace Contracts;

/// <summary>
/// A payment has reached a final status other than captured: declined or voided (stage 5). No money moved, so the
/// ledger never hears of it; the history of payments and, in stage 6, the webhooks do. A captured payment has
/// <see cref="PaymentCaptured"/> instead, which they read as well: one event per final payment, in two topics.
/// Why not one event for every final status: a second outbox row for each captured payment doubled the rows the relay
/// moves, and under the load of the matrix the outbox fell a minute behind (docs/load-testing.md, «Этап 5»).
/// Published by Payments.Api through the outbox, built by the statement that writes the status from the row it has
/// just written, so there is one per payment, only if the transition really happened, and every field is what the
/// database holds. Amounts are in minor units (kopecks).
/// </summary>
/// <param name="Status">One of <see cref="PaymentFinalStatus"/>: the status as the database spells it.</param>
/// <param name="FinalizedAt">When the final status was written, by the clock of the database of Payments.Api.</param>
public sealed record PaymentFinalized(
    Guid PaymentId,
    long MerchantId,
    long Amount,
    string Currency,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset FinalizedAt);

/// <summary>The statuses that travel in <see cref="PaymentFinalized"/>; 'captured' travels in <see cref="PaymentCaptured"/>.</summary>
public static class PaymentFinalStatus
{
    public const string Declined = "declined";
    public const string Voided = "voided";
}
