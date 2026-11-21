namespace Payments.Api.Antifraud;

public sealed class AntifraudOptions
{
    public AntifraudMode Mode { get; set; } = AntifraudMode.Fake;

    public Uri BaseAddress { get; set; } = new("http://localhost:5400/");
}
