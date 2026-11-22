namespace Payments.Api.Antifraud;

/// <summary>
/// The answer of the antifraud check. Score, the codes of the rules that fired and the version of the rules or model
/// come only with Allow and Deny; they go to the logs and metrics, the saga acts on the outcome alone.
/// </summary>
public sealed record AntifraudDecision(
    AntifraudOutcome Outcome,
    double? Score = null,
    IReadOnlyList<string>? Reasons = null,
    string? ModelVersion = null)
{
    /// <summary>No decision on this one check: a 5xx or a timeout. The next check may well get one.</summary>
    public static readonly AntifraudDecision Unknown = new(AntifraudOutcome.Unknown);

    /// <summary>
    /// No decision because the circuit breaker of the client is open: enough checks have failed lately for the
    /// antifraud to be taken as down, and the check did not reach it.
    /// </summary>
    public static readonly AntifraudDecision Unavailable = new(AntifraudOutcome.Unknown) { CircuitOpen = true };

    /// <summary>
    /// Set with <see cref="AntifraudOutcome.Unknown"/> alone, when the breaker is open. Recovery pauses then: the checks
    /// of the rest of its pass would fail the same way (PaymentRecovery).
    /// </summary>
    public bool CircuitOpen { get; private init; }
}
