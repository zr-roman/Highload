namespace Payments.Api.Payments;

public enum PaymentStatus
{
    /// <summary>
    /// Создан
    /// </summary>
    Created,

    /// <summary>
    /// Проведен
    /// </summary>
    Captured,

    /// <summary>
    /// Отклонен
    /// </summary>
    Declined,
}

public sealed record Payment(
    Guid Id,
    long MerchantId,
    string IdempotencyKey,
    string RequestHash,
    long Amount,
    string Currency,
    long? Fee,
    PaymentStatus Status,
    DateTimeOffset CreatedAt);

internal static class PaymentStatusDb
{
    public static string ToDb(this PaymentStatus status) => status switch
    {
        PaymentStatus.Created => "created",
        PaymentStatus.Captured => "captured",
        PaymentStatus.Declined => "declined",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null),
    };

    public static PaymentStatus Parse(string value) => value switch
    {
        "created" => PaymentStatus.Created,
        "captured" => PaymentStatus.Captured,
        "declined" => PaymentStatus.Declined,
        _ => throw new InvalidOperationException($"Unknown payment status '{value}'"),
    };
}
