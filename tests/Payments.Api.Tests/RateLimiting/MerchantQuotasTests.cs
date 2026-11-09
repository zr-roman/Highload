using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Payments.Api.RateLimiting;
using Payments.Api.Tests.Infrastructure;

namespace Payments.Api.Tests.RateLimiting;

/// <summary>Quotas of large merchants over the default limit, and how a change of them is taken without a restart.</summary>
public sealed class MerchantQuotasTests
{
    [Fact]
    public void A_Merchant_With_A_Quota_Gets_It_And_The_Others_Get_The_Default()
    {
        using var quotas = Create(Settings(overrides: (1, 1000, 2000)));

        Assert.Equal(new MerchantQuota(1000, 2000, IsOverride: true), quotas.Of(1));
        Assert.Equal(new MerchantQuota(200, 400, IsOverride: false), quotas.Of(2));
    }

    // The way the stand sets them: RateLimiting__Merchant__Overrides__1__TokensPerSecond in the environment.
    [Fact]
    public void Quotas_Bind_From_The_Configuration_By_MerchantId()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["RateLimiting:Merchant:Overrides:1:TokensPerSecond"] = "1000",
            ["RateLimiting:Merchant:Overrides:1:Burst"] = "2000",
        }).Build();

        var settings = configuration.GetSection("RateLimiting:Merchant").Get<MerchantRateLimitOptions>()!;

        Assert.Equal(new MerchantQuota(1000, 2000, IsOverride: true), settings.QuotaOf(1));
    }

    [Fact]
    public void A_New_Quota_Applies_Without_A_Restart()
    {
        var options = new TestOptionsMonitor<MerchantRateLimitOptions>(Settings());
        using var quotas = new MerchantQuotas(options, NullLogger<MerchantQuotas>.Instance);

        options.Set(Settings(overrides: (1, 1000, 2000)));

        Assert.True(quotas.Of(1).IsOverride);
    }

    // A typo in appsettings.json must not take the limit down: the last good settings stay.
    [Fact]
    public void A_Change_That_Does_Not_Validate_Is_Ignored()
    {
        var options = new TestOptionsMonitor<MerchantRateLimitOptions>(Settings(overrides: (1, 1000, 2000)));
        using var quotas = new MerchantQuotas(options, NullLogger<MerchantQuotas>.Instance);

        options.Set(Settings(overrides: (1, 0, 2000)));

        Assert.Equal(1000, quotas.Of(1).TokensPerSecond);
    }

    [Fact]
    public void Settings_That_Do_Not_Validate_Fail_At_Once()
    {
        var error = Assert.Throws<OptionsValidationException>(() => Create(Settings(overrides: (7, 1000, 0))));

        Assert.Contains("7", error.Message);
    }

    private static MerchantQuotas Create(MerchantRateLimitOptions settings) =>
        new(new TestOptionsMonitor<MerchantRateLimitOptions>(settings), NullLogger<MerchantQuotas>.Instance);

    private static MerchantRateLimitOptions Settings(params (long Merchant, double TokensPerSecond, int Burst)[] overrides) => new()
    {
        TokensPerSecond = 200,
        Burst = 400,
        Overrides = overrides.ToDictionary(
            quota => quota.Merchant,
            quota => new MerchantQuotaOptions { TokensPerSecond = quota.TokensPerSecond, Burst = quota.Burst }),
    };
}
