using Microsoft.Extensions.Options;
using Payments.Api.Payments;

namespace Payments.Api.Acquiring;

/// <summary>
/// Stage 1 stand-in for the bank, inside the process. Stage 2 replaces it with an HTTP client
/// to a separate bank emulator, with a timeout, retries and a circuit breaker.
/// </summary>
public sealed class FakeAcquirer(IOptions<AcquirerOptions> options) : IAcquirer
{
    public async Task<AcquirerDecision> AuthorizeAsync(Payment payment, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        if (settings.LatencyMs > 0)
            await Task.Delay(settings.LatencyMs, cancellationToken);

        return new AcquirerDecision(Random.Shared.Next(100) >= settings.DeclineRatePercent ? AcquirerOutcome.Approved : AcquirerOutcome.Declined);
    }

    public Task<AcquirerDecision?> FindDecisionAsync(Guid paymentId, CancellationToken cancellationToken)
    {
        return Task.FromResult<AcquirerDecision?>(null);
    }
}
