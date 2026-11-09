using Microsoft.Extensions.Options;

namespace Payments.Api.RateLimiting;

/// <summary>
/// The merchant limits as configured now. RateLimiting:Merchant is read again when appsettings.json changes, so a
/// quota changes without a restart; the token bucket in Redis takes the new rate from the next request on.
/// </summary>
/// <remarks>
/// Not a Validate on the options themselves: IOptionsMonitor would then throw on every read after a reload that does
/// not validate, and every payment would fail on a typo in the file. Settings that do not validate fail the start
/// (Program.cs resolves this class at startup); a reload that does not validate is logged, and the last good
/// settings stay.
/// </remarks>
public sealed class MerchantQuotas : IDisposable
{
    private readonly IDisposable? _subscription;
    private volatile MerchantRateLimitOptions _current;

    public MerchantQuotas(IOptionsMonitor<MerchantRateLimitOptions> options, ILogger<MerchantQuotas> logger)
    {
        var current = options.CurrentValue;
        if (current.Validate() is { } error)
            throw new OptionsValidationException(nameof(MerchantRateLimitOptions), typeof(MerchantRateLimitOptions), [error]);
        _current = current;

        _subscription = options.OnChange(changed =>
        {
            if (changed.Validate() is { } error)
            {
                logger.LogError("Rate limiter: the new settings are ignored, the last good ones stay: {Error}", error);
                return;
            }

            _current = changed;
            logger.LogInformation("Rate limiter: settings reloaded, {Overrides} merchant quotas", changed.Overrides.Count);
        });
    }

    public bool Enabled => _current.Enabled;

    public MerchantQuota Of(long merchantId) => _current.QuotaOf(merchantId);

    public void Dispose() => _subscription?.Dispose();
}
