using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Payments.Api.Acquiring;
using Payments.Api.RateLimiting;

namespace Payments.Api.Tests.Infrastructure;

/// <summary>
/// HttpAcquirer registered the way Program.cs does it (AddHttpAcquirer), over StubBankHandler instead of the network,
/// with the resilience settings from the service's appsettings.json. Only the attempt timeout is shortened,
/// so a hanging bank costs a test a fraction of a second, and <see cref="LargeMerchant"/> gets a quota of its own.
/// </summary>
public sealed class AcquirerHost : IDisposable
{
    public static readonly TimeSpan AttemptTimeout = TimeSpan.FromMilliseconds(100);

    /// <summary>A merchant with a quota of its own, and so with a bank pipeline of its own (BankPipeline).</summary>
    public const long LargeMerchant = 7;

    private readonly ServiceProvider services;

    public AcquirerHost(StubBankHandler bank)
    {
        // appsettings.json of Payments.Api is copied next to the tests, with the project reference.
        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json")
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Acquirer:Resilience:AttemptTimeout:Timeout"] = AttemptTimeout.ToString(),
                [$"RateLimiting:Merchant:Overrides:{LargeMerchant}:TokensPerSecond"] = "1000",
                [$"RateLimiting:Merchant:Overrides:{LargeMerchant}:Burst"] = "2000",
            })
            .Build();

        var acquirer = configuration.GetSection("Acquirer");
        Attempts = 1 + acquirer.GetValue<int>("Resilience:Retry:MaxRetryAttempts");
        TotalTimeout = acquirer.GetValue<TimeSpan>("Resilience:TotalRequestTimeout:Timeout");
        CircuitMinimumThroughput = acquirer.GetValue<int>("Resilience:CircuitBreaker:MinimumThroughput");

        var collection = new ServiceCollection();
        collection.AddOptions<MerchantRateLimitOptions>().Bind(configuration.GetSection("RateLimiting:Merchant"));
        collection.AddSingleton<MerchantQuotas>();
        collection.AddHttpAcquirer(acquirer).ConfigurePrimaryHttpMessageHandler(() => bank);
        services = collection.BuildServiceProvider();

        Acquirer = services.GetRequiredService<IAcquirer>();
    }

    public IAcquirer Acquirer { get; }

    /// <summary>The first try plus the retries, from the service's settings.</summary>
    public int Attempts { get; }

    public TimeSpan TotalTimeout { get; }

    /// <summary>Failed attempts within the sampling window that open the circuit.</summary>
    public int CircuitMinimumThroughput { get; }

    public void Dispose() => services.Dispose();
}
