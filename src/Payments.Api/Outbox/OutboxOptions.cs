namespace Payments.Api.Outbox;

public sealed class OutboxOptions
{
    public int BatchSize { get; set; } = 500;

    /// <summary>Pause after the first empty poll; it doubles on every further empty poll.</summary>
    public int IdleDelayMs { get; set; } = 20;

    /// <summary>The longest pause between polls of an empty outbox.</summary>
    public int MaxIdleDelayMs { get; set; } = 1000;
}
