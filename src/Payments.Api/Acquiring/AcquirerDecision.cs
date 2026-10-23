namespace Payments.Api.Acquiring;

public sealed record AcquirerDecision(AcquirerOutcome Outcome, string? AuthorizationCode = null);
