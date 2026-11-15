namespace Payments.Api.Outbox;

public sealed class OutboxOptions
{
    public int BatchSize { get; set; } = 500;

    /// <summary>Pause after the first empty poll; it doubles on every further empty poll.</summary>
    public int IdleDelayMs { get; set; } = 20;

    /// <summary>The longest pause between polls of an empty outbox.</summary>
    public int MaxIdleDelayMs { get; set; } = 1000;

    /// <summary>
    /// Pause after a batch that was published: a brake for the catch-up after Kafka was down, so that draining
    /// a long outbox does not take Postgres and CPU from incoming payments. 0: no pause, the relay drains as fast as it can.
    /// With a pause the relay moves at most BatchSize rows per pause; that has to stay above the incoming rate,
    /// or the outbox never empties.
    /// </summary>
    public int BusyDelayMs { get; set; }
}
