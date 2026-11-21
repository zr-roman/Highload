using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Payments.Api.Acquiring;
using Payments.Api.Antifraud;
using Payments.Api.RateLimiting;

namespace Payments.Api.Tests.Infrastructure;

/// <summary>
/// HttpAntifraud registered the way Program.cs does it (AddHttpAntifraud), over StubAntifraudHandler instead of the
/// network, with the resilience settings from the service's appsettings.json as they are: they are short already.
/// The bank client sits in the same container, as in the service, to show the two pipelines do not share a breaker.
/// </summary>
public sealed class AntifraudHost : IDisposable
{
    /// <summary>A merchant with a quota of its own: "large" for antifraud.</summary>
    public const long LargeMerchant = 7;

    public const double LargeMerchantQuota = 1000;

    private readonly ServiceProvider services;

    public AntifraudHost(StubAntifraudHandler antifraud, StubBankHandler bank)
    {
        // appsettings.json of Payments.Api is copied next to the tests, with the project reference.
        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json")
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"RateLimiting:Merchant:Overrides:{LargeMerchant}:TokensPerSecond"] = LargeMerchantQuota.ToString(),
                [$"RateLimiting:Merchant:Overrides:{LargeMerchant}:Burst"] = "2000",
            })
            .Build();

        var section = configuration.GetSection("Antifraud");
        Attempts = 1 + section.GetValue<int>("Resilience:Retry:MaxRetryAttempts");
        TotalTimeout = section.GetValue<TimeSpan>("Resilience:TotalRequestTimeout:Timeout");
        CircuitMinimumThroughput = section.GetValue<int>("Resilience:CircuitBreaker:MinimumThroughput");

        var collection = new ServiceCollection();
        collection.AddLogging();
        collection.AddOptions<MerchantRateLimitOptions>().Bind(configuration.GetSection("RateLimiting:Merchant"));
        collection.AddSingleton<MerchantQuotas>();
        collection.AddHttpAntifraud(section).ConfigurePrimaryHttpMessageHandler(() => antifraud);
        collection.AddHttpAcquirer(configuration.GetSection("Acquirer")).ConfigurePrimaryHttpMessageHandler(() => bank);
        services = collection.BuildServiceProvider();

        Antifraud = services.GetRequiredService<IAntifraud>();
        Acquirer = services.GetRequiredService<IAcquirer>();
    }

    public IAntifraud Antifraud { get; }

    public IAcquirer Acquirer { get; }

    /// <summary>The first try plus the retries, from the service's settings.</summary>
    public int Attempts { get; }

    public TimeSpan TotalTimeout { get; }

    /// <summary>Failed attempts within the sampling window that open the circuit.</summary>
    public int CircuitMinimumThroughput { get; }

    public void Dispose() => services.Dispose();
}
