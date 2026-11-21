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
    public static readonly AntifraudDecision Unknown = new(AntifraudOutcome.Unknown);
}
