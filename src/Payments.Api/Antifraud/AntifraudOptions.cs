namespace Payments.Api.Antifraud;

public sealed class AntifraudOptions
{
    public AntifraudMode Mode { get; set; } = AntifraudMode.Fake;

    public Uri BaseAddress { get; set; } = new("http://localhost:5400/");

    /// <summary>
    /// Fail open, in kopecks: when the antifraud gives no decision in the request, a payment below this amount is
    /// captured unchecked, and one at it or above waits in 'authorized' for recovery to ask again. Recovery never fails
    /// open: nobody waits for it. 0 turns fail open off; a negative value fails the start (Program.cs).
    /// </summary>
    public long FailOpenBelowAmount { get; set; }
}
