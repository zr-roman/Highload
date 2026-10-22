using Payments.Api.Payments;

namespace Payments.Api.Acquiring;

/// <summary>The bank that approves or declines a card payment.</summary>
public interface IAcquirer
{
    Task<AcquirerDecision> AuthorizeAsync(Payment payment, CancellationToken cancellationToken);
    Task<AcquirerDecision?> FindDecisionAsync(Guid paymentId, CancellationToken cancellationToken);
}
