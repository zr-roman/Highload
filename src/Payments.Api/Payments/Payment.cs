namespace Payments.Api.Payments;

public enum PaymentStatus
{
    /// <summary>
    /// Создан
    /// </summary>
    Created,

    /// <summary>
    /// Банк одобрил, решение антифрода ещё не принято (сага, этап 4)
    /// </summary>
    Authorized,

    /// <summary>
    /// Проведен
    /// </summary>
    Captured,

    /// <summary>
    /// Отклонен
    /// </summary>
    Declined,

    /// <summary>
    /// Авторизация отменена в банке (void), деньги не списаны
    /// </summary>
    Voided,
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
        PaymentStatus.Authorized => "authorized",
        PaymentStatus.Captured => "captured",
        PaymentStatus.Declined => "declined",
        PaymentStatus.Voided => "voided",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null),
    };

    public static PaymentStatus Parse(string value) => value switch
    {
        "created" => PaymentStatus.Created,
        "authorized" => PaymentStatus.Authorized,
        "captured" => PaymentStatus.Captured,
        "declined" => PaymentStatus.Declined,
        "voided" => PaymentStatus.Voided,
        _ => throw new InvalidOperationException($"Unknown payment status '{value}'"),
    };
}
