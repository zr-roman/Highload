namespace Payments.Api.Antifraud;

/// <summary>What the antifraud check says about a payment the bank has approved (stage 4, the saga).</summary>
public enum AntifraudOutcome
{
    /// <summary>The payment goes on to capture.</summary>
    Allow,

    /// <summary>The payment is refused: the saga voids its authorization at the bank.</summary>
    Deny,

    /// <summary>
    /// No decision: 5xx (503 is Redis down at antifraud), a timeout or an open circuit. The saga (PaymentSaga) decides
    /// by the amount in the request: fail open below the threshold, otherwise the payment waits in 'authorized'.
    /// Recovery never fails open.
    /// </summary>
    Unknown,
}
