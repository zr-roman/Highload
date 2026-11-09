namespace Payments.Api.RateLimiting;

public sealed class MerchantRateLimitOptions
{
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// С какой скоростью пополняется bucket
    /// </summary>
    public double TokensPerSecond { get; set; } = 200;

    /// <summary>
    ///  Ёмкость bucket'а, то есть сколько запросов можно отправить разом после паузы
    /// </summary>
    public int Burst { get; set; } = 400;

    /// <summary>
    /// Quotas of large merchants by merchantId, a part of their contract. The other merchants get
    /// <see cref="TokensPerSecond"/> and <see cref="Burst"/>.
    /// </summary>
    public Dictionary<long, MerchantQuotaOptions> Overrides { get; set; } = [];

    public MerchantQuota QuotaOf(long merchantId) =>
        Overrides.TryGetValue(merchantId, out var quota)
            ? new MerchantQuota(quota.TokensPerSecond, quota.Burst, IsOverride: true)
            : new MerchantQuota(TokensPerSecond, Burst, IsOverride: false);

    /// <summary>What is wrong with the settings, or null if nothing is.</summary>
    public string? Validate()
    {
        if (TokensPerSecond <= 0 || Burst <= 0)
            return "RateLimiting:Merchant: TokensPerSecond and Burst must be positive";

        var wrong = Overrides.Where(merchant => merchant.Value.TokensPerSecond <= 0 || merchant.Value.Burst <= 0).Select(merchant => merchant.Key).ToList();
        return wrong.Count > 0
            ? $"RateLimiting:Merchant:Overrides: TokensPerSecond and Burst must be positive, merchants {string.Join(", ", wrong)}"
            : null;
    }
}

public sealed class MerchantQuotaOptions
{
    public double TokensPerSecond { get; set; }

    public int Burst { get; set; }
}

/// <summary>The limit of one merchant: its own from the overrides, or the default one.</summary>
public readonly record struct MerchantQuota(double TokensPerSecond, int Burst, bool IsOverride);
