using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Payments.Api.Payments;

public static class PaymentRules
{
    public const string SupportedCurrency = "RUB";

    /// <summary>1 billion roubles in kopecks: keeps the fee calculation far away from long overflow.</summary>
    public const long MaxAmount = 100_000_000_000;

    public const int MaxIdempotencyKeyLength = 64;

    public static Dictionary<string, string[]> Validate(CreatePaymentRequest request, string? idempotencyKey)
    {
        var errors = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > MaxIdempotencyKeyLength)
            errors["Idempotency-Key"] = [$"The header is required and must be at most {MaxIdempotencyKeyLength} characters."];
        if (request.MerchantId <= 0)
            errors[nameof(request.MerchantId)] = ["Must be positive."];
        if (request.Amount is <= 0 or > MaxAmount)
            errors[nameof(request.Amount)] = [$"Must be between 1 and {MaxAmount} kopecks."];
        if (request.Currency != SupportedCurrency)
            errors[nameof(request.Currency)] = [$"Only {SupportedCurrency} is supported."];
        return errors;
    }

    /// <summary>
    /// Fingerprint of the request body. The same Idempotency-Key with a different body is an error,
    /// not a replay.
    /// </summary>
    public static string ComputeRequestHash(CreatePaymentRequest request)
    {
        var canonical = string.Create(CultureInfo.InvariantCulture,
            $"{request.MerchantId}|{request.Amount}|{request.Currency}");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }
}

public sealed class FeeOptions
{
    /// <summary>Fee in basis points: 200 = 2 %.</summary>
    public int Bps { get; set; } = 200;

    /// <summary>Integer arithmetic in kopecks, rounded down.</summary>
    public long Calculate(long amount) => amount * Bps / 10_000;
}
