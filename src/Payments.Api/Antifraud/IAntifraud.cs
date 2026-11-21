using Payments.Api.Payments;

namespace Payments.Api.Antifraud;

/// <summary>The antifraud check of a payment the bank has approved.</summary>
public interface IAntifraud
{
    /// <summary>
    /// Allow or deny the payment. Idempotent at antifraud by payment id: a repeated check gets the decision stored the
    /// first time, so a check after <see cref="AntifraudOutcome.Unknown"/> is safe to repeat.
    /// </summary>
    Task<AntifraudDecision> CheckAsync(Payment payment, CancellationToken cancellationToken);
}
