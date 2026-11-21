using Payments.Api.Payments;

namespace Payments.Api.Antifraud;

/// <summary>
/// Allows every payment, in the process: for the load matrix and the tests that run the platform without the
/// antifraud service (Antifraud:Mode = Fake).
/// </summary>
public sealed class FakeAntifraud : IAntifraud
{
    private static readonly AntifraudDecision Allow = new(AntifraudOutcome.Allow, Score: 0, Reasons: [], ModelVersion: "fake");

    public Task<AntifraudDecision> CheckAsync(Payment payment, CancellationToken cancellationToken) =>
        Task.FromResult(Allow);
}
