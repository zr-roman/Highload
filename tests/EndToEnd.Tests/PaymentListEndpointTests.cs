using System.Net;
using System.Net.Http.Json;
using EndToEnd.Tests.Infrastructure;
using Microsoft.AspNetCore.Mvc;
using Payments.Api.Payments;
using Payments.Api.Tests.Infrastructure;

namespace EndToEnd.Tests;

/// <summary>
/// GET /merchants/{id}/payments through the HTTP of Payments.Api, as the service runs: the pages of payments made by
/// POST /payments, a 400 for a cursor or a parameter it did not give, and the index SchemaAfterStart builds after
/// the start.
/// </summary>
[Collection("Platform")]
public sealed class PaymentListEndpointTests(PlatformFixture platform)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Merchant_Gets_Its_Payments_Page_By_Page_Newest_First()
    {
        // A merchant of its own, far from those of MoneyAddsUpTests, so the list has only this test's payments.
        var merchant = 1_000_000 + Random.Shared.Next(1_000_000);
        var made = new List<Guid>();
        for (var i = 0; i < 5; i++)
            made.Add(await PayAsync(merchant, amount: 10_000 + i));

        var first = await PageAsync($"/merchants/{merchant}/payments?limit=3");
        var second = await PageAsync($"/merchants/{merchant}/payments?limit=3&cursor={first.NextCursor}");

        Assert.Equal(3, first.Items.Count);
        Assert.NotNull(first.NextCursor);
        Assert.Equal(2, second.Items.Count);
        Assert.Null(second.NextCursor);

        // Made one after another, so newest first is the reverse; every item is the merchant's.
        var listed = first.Items.Concat(second.Items).ToList();
        Assert.Equal(Enumerable.Reverse(made), listed.Select(item => item.Id));
        Assert.All(listed, item => Assert.Equal(merchant, item.MerchantId));
    }

    [Fact]
    public async Task Merchant_Without_Payments_Gets_An_Empty_Page()
    {
        var page = await PageAsync($"/merchants/{3_000_000 + Random.Shared.Next(1_000_000)}/payments");

        Assert.Empty(page.Items);
        Assert.Null(page.NextCursor);
    }

    [Theory]
    [InlineData("cursor=not-a-cursor", "cursor")]
    [InlineData("limit=101", "limit")]
    [InlineData("status=paid", "status")]
    [InlineData("from=2026-11-24T12:00:00Z&to=2026-11-24T11:00:00Z", "to")]
    public async Task Parameter_The_Api_Does_Not_Take_Is_A_400_That_Names_It(string parameters, string name)
    {
        using var response = await platform.Payments.GetAsync($"/merchants/1/payments?{parameters}", Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ValidationProblemDetails>(Ct);
        Assert.Equal([name], problem!.Errors.Keys);
    }

    [Fact]
    public async Task Index_Of_The_List_Is_Built_After_The_Start()
    {
        await Eventually.WaitUntilAsync(async () =>
        {
            await using var command = platform.PaymentsDb.CreateCommand("""
                SELECT coalesce(bool_and(i.indisvalid), false)
                FROM pg_class c JOIN pg_index i ON i.indexrelid = c.oid
                WHERE c.relname = 'payments_merchant_created'
                """);
            return (bool)(await command.ExecuteScalarAsync(Ct))!;
        }, TimeSpan.FromSeconds(60));
    }

    private async Task<Guid> PayAsync(long merchantId, long amount)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/payments")
        {
            Content = JsonContent.Create(new CreatePaymentRequest(merchantId, amount, "RUB")),
        };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());

        using var response = await platform.Payments.SendAsync(request, Ct);
        Assert.True(response.IsSuccessStatusCode, $"POST /payments answered {(int)response.StatusCode}");
        return (await response.Content.ReadFromJsonAsync<PaymentResponse>(Ct))!.Id;
    }

    private async Task<PaymentPage> PageAsync(string path)
    {
        using var response = await platform.Payments.GetAsync(path, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<PaymentPage>(Ct))!;
    }
}
