using Payments.Api.Payments;

namespace Payments.Api.Acquiring;

/// <summary>The bank that approves or declines a card payment, and voids an approval it gave.</summary>
public interface IAcquirer
{
    Task<AcquirerDecision> AuthorizeAsync(Payment payment, CancellationToken cancellationToken);
    Task<AcquirerDecision?> FindDecisionAsync(Guid paymentId, CancellationToken cancellationToken);

    /// <summary>
    /// Voids the approved authorization of the payment: the bank releases the hold on the buyer's money.
    /// Idempotent at the bank, so a repeated void after <see cref="VoidOutcome.Unknown"/> is safe.
    /// Takes the whole payment, not its id: the call goes through the merchant's own bank pipeline.
    /// </summary>
    Task<VoidOutcome> VoidAsync(Payment payment, CancellationToken cancellationToken);
}
