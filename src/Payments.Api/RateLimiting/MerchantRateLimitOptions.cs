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
}
