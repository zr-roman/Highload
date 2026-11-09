using Payments.Api.RateLimiting;

namespace Payments.Api.Acquiring;

/// <summary>
/// Which resilience pipeline of the bank client a call goes through. Each pipeline has its own circuit breaker and its
/// own limit of concurrent calls, so a bank that fails one large merchant opens the breaker of that merchant alone.
/// </summary>
/// <remarks>
/// A large merchant, one with a quota of its own (RateLimiting:Merchant:Overrides), gets a pipeline of its own: there
/// are few of them, and each makes enough calls for its breaker to judge. The others share one pipeline. A pipeline per
/// merchant would be thousands of them, and a small merchant never makes the MinimumThroughput calls within the
/// SamplingDuration that its breaker needs to open.
/// </remarks>
public static class BankPipeline
{
    public const string Shared = "shared";

    /// <summary>Where HttpAcquirer puts the pipeline of a request, for the selector of AddHttpAcquirer to read.</summary>
    public static readonly HttpRequestOptionsKey<string> Key = new("payments.bank_pipeline");

    public static string Of(long merchantId, MerchantQuota quota) =>
        quota.IsOverride ? $"merchant-{merchantId}" : Shared;
}
