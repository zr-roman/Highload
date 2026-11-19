using System.Net;
using EndToEnd.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace EndToEnd.Tests;

/// <summary>
/// The services start as in the cluster: in Production, with no CORS origins configured. A stray app.UseCors()
/// once passed every test in Development and failed Ledger at startup in the cluster; these checks would have caught it.
/// </summary>
[Collection("Platform")]
public sealed class ProductionStartupTests(PlatformFixture platform)
{
    // The address of the frontend's dev server, the origin appsettings.Development.json allows.
    private const string FrontendDevOrigin = "http://localhost:5173";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void Services_Run_In_Production_As_In_The_Cluster()
    {
        Assert.Equal(Environments.Production, platform.PaymentsServices.GetRequiredService<IHostEnvironment>().EnvironmentName);
        Assert.Equal(Environments.Production, platform.LedgerServices.GetRequiredService<IHostEnvironment>().EnvironmentName);
    }

    [Fact]
    public async Task Without_Cors_Origins_Payments_Api_Starts_And_Allows_No_Origin() =>
        await AssertStartedWithoutCorsAsync(platform.Payments);

    [Fact]
    public async Task Without_Cors_Origins_Ledger_Starts_And_Allows_No_Origin() =>
        await AssertStartedWithoutCorsAsync(platform.Ledger);

    private static async Task AssertStartedWithoutCorsAsync(HttpClient service)
    {
        // Even the origin Development allows gets no CORS answer: in the cluster the UI and the APIs share one address
        // behind Traefik, so the browser never makes a cross-origin call.
        using var request = new HttpRequestMessage(HttpMethod.Get, "/health/live");
        request.Headers.Add("Origin", FrontendDevOrigin);

        using var response = await service.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
    }
}
