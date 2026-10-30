namespace Payments.Api.Acquiring;

public static class AcquirerServiceCollectionExtensions
{
    /// <summary>
    /// The bank client: HttpAcquirer behind the standard resilience pipeline, both configured from the Acquirer section.
    /// Program.cs and the tests register it here, so the tests exercise the pipeline the service actually runs.
    /// </summary>
    public static IHttpClientBuilder AddHttpAcquirer(this IServiceCollection services, IConfigurationSection acquirer)
    {
        var baseAddress = acquirer.Get<AcquirerOptions>()?.BaseAddress ?? new AcquirerOptions().BaseAddress;

        var client = services.AddHttpClient<IAcquirer, HttpAcquirer>(http => http.BaseAddress = baseAddress);

        // Retrying POST is safe here only because the bank is idempotent by paymentId.
        client.AddStandardResilienceHandler(acquirer.GetSection("Resilience"));

        return client;
    }
}
