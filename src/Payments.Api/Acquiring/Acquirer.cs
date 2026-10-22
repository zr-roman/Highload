using Microsoft.Extensions.Options;
using Payments.Api.Payments;

namespace Payments.Api.Acquiring;

/// <summary>The bank that approves or declines a card payment.</summary>
public interface IAcquirer
{
    Task<AcquirerDecision> AuthorizeAsync(Payment payment, CancellationToken cancellationToken);
}

public sealed record AcquirerDecision(bool Approved);

public sealed class AcquirerOptions
{
    public int LatencyMs { get; set; }
    public int DeclineRatePercent { get; set; } = 2;
}

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

        return new AcquirerDecision(Approved: Random.Shared.Next(100) >= settings.DeclineRatePercent);
    }
}
