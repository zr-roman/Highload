namespace Payments.Api.Acquiring;

/// <summary>What the bank says about a payment.</summary>
public enum AcquirerOutcome
{
    Approved,
    Declined,

    /// <summary>The call failed or timed out, or the circuit is open: the bank may or may not have decided.</summary>
    Unknown,

    /// <summary>
    /// Approved and then voided (the saga's compensation, stage 4): no money is held. The bank answers so to a lookup
    /// and to a repeated authorization of a voided payment; approving it again is impossible.
    /// </summary>
    Voided,
}
