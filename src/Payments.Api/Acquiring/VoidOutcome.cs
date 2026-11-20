namespace Payments.Api.Acquiring;

/// <summary>What the bank says to a void of an approved authorization (the saga's compensation, stage 4).</summary>
public enum VoidOutcome
{
    /// <summary>The authorization is voided, now or before: no money is held.</summary>
    Voided,

    /// <summary>
    /// The bank has no decision for the payment (404). In the normal flow a payment to void was approved, so this
    /// means the bank lost it: the emulator keeps decisions in memory and forgets them on restart.
    /// Nothing is held, so nothing is left to void; the orchestrator decides what to record (task 5).
    /// </summary>
    NotFound,

    /// <summary>The bank declined the payment (409): there is no authorization to void.</summary>
    Conflict,

    /// <summary>The call failed or timed out, or the circuit is open: the void may or may not have happened.</summary>
    Unknown,
}
