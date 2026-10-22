namespace Payments.Api.Acquiring;

public sealed class AcquirerOptions
{
    public int LatencyMs { get; set; }
    public int DeclineRatePercent { get; set; } = 2;

    public Uri BaseAddress { get; set; } = new("http://localhost:5300/");
    public AcquirerMode Mode { get; set; } = AcquirerMode.Fake;
}