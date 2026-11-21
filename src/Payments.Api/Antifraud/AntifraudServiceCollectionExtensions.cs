using Microsoft.Extensions.Http.Resilience;

namespace Payments.Api.Antifraud;

public static class AntifraudServiceCollectionExtensions
{
    /// <summary>
    /// The antifraud client: HttpAntifraud behind a standard resilience pipeline of its own, both configured from the
    /// Antifraud section. Program.cs and the tests register it here, so the tests exercise the pipeline the service runs.
    /// </summary>
    /// <remarks>
    /// Its own pipeline, not the bank's: its own timeouts, sized to a check that answers in milliseconds, and its own
    /// circuit breaker, so an antifraud that is down opens no circuit on the way to the bank, and the other way round.
    /// </remarks>
    public static IHttpClientBuilder AddHttpAntifraud(this IServiceCollection services, IConfigurationSection antifraud)
    {
        var baseAddress = antifraud.Get<AntifraudOptions>()?.BaseAddress ?? new AntifraudOptions().BaseAddress;
        var client = services.AddHttpClient<IAntifraud, HttpAntifraud>(http => http.BaseAddress = baseAddress);
        // Retrying POST is safe here only because antifraud stores its decision by paymentId.
        client.AddStandardResilienceHandler(antifraud.GetSection("Resilience"));
        return client;
    }
}
