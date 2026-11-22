using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Payments.Api.Acquiring;
using Payments.Api.Antifraud;
using Payments.Api.Payments;
using Payments.Api.Tests.Infrastructure;

namespace Payments.Api.Tests.Antifraud;

/// <summary>
/// HttpAntifraud with the real resilience pipeline over a stub service, without the network: what each answer
/// becomes, the contract of the request, and how retries, timeouts and the circuit breaker treat failures.
/// </summary>
public sealed class HttpAntifraudTests : IDisposable
{
    private readonly StubAntifraudHandler antifraud = new();
    private readonly StubBankHandler bank = new();
    private readonly AntifraudHost host;

    public HttpAntifraudTests() => host = new AntifraudHost(antifraud, bank);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => host.Dispose();

    [Theory]
    [InlineData("allow", AntifraudOutcome.Allow)]
    [InlineData("deny", AntifraudOutcome.Deny)]
    public async Task Decision_Becomes_The_Outcome_With_Its_Details(string decision, AntifraudOutcome outcome)
    {
        antifraud.Decide(decision, score: 0.9, reasons: ["velocity"], modelVersion: "rules-2");

        var result = await host.Antifraud.CheckAsync(NewPayment(), Ct);

        Assert.Equal(outcome, result.Outcome);
        Assert.Equal(0.9, result.Score);
        Assert.Equal(new[] { "velocity" }, result.Reasons);
        Assert.Equal("rules-2", result.ModelVersion);
        Assert.Single(antifraud.Requests);
    }

    [Fact]
    public async Task Check_Of_A_Standard_Merchant_Sends_The_Payment_Without_Class_And_Quota()
    {
        var payment = NewPayment();
        antifraud.Decide("allow");

        await host.Antifraud.CheckAsync(payment, Ct);

        // The contract with the Python service: the path, the camelCase fields, createdAt with its offset.
        var request = Assert.Single(antifraud.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/v1/checks", request.Path);

        using var body = JsonDocument.Parse(request.Body!);
        var root = body.RootElement;
        Assert.Equal(payment.Id, root.GetProperty("paymentId").GetGuid());
        Assert.Equal(payment.MerchantId, root.GetProperty("merchantId").GetInt64());
        Assert.Equal(payment.Amount, root.GetProperty("amount").GetInt64());
        Assert.Equal(payment.Currency, root.GetProperty("currency").GetString());
        Assert.Matches(@"(Z|[+-]\d\d:\d\d)$", root.GetProperty("createdAt").GetString());
        Assert.Equal(payment.CreatedAt, root.GetProperty("createdAt").GetDateTimeOffset());

        // Antifraud rejects a quota without the large class: the standard merchant sends neither field.
        Assert.False(root.TryGetProperty("merchantClass", out _));
        Assert.False(root.TryGetProperty("quotaPerSecond", out _));
    }

    [Fact]
    public async Task Check_Of_A_Large_Merchant_Sends_Its_Class_And_Quota()
    {
        antifraud.Decide("allow");

        await host.Antifraud.CheckAsync(NewPayment(AntifraudHost.LargeMerchant), Ct);

        using var body = JsonDocument.Parse(Assert.Single(antifraud.Requests).Body!);
        Assert.Equal("large", body.RootElement.GetProperty("merchantClass").GetString());
        Assert.Equal(AntifraudHost.LargeMerchantQuota, body.RootElement.GetProperty("quotaPerSecond").GetDouble());
    }

    [Fact]
    public async Task Failed_Attempt_Is_Retried_With_The_Same_Payment()
    {
        var payment = NewPayment();
        antifraud.Status(HttpStatusCode.InternalServerError).Decide("deny");

        Assert.Equal(AntifraudOutcome.Deny, (await host.Antifraud.CheckAsync(payment, Ct)).Outcome);

        // Safe only because antifraud stores its decision by paymentId: the retry gets the same decision.
        Assert.Equal(2, antifraud.Requests.Count);
        Assert.All(antifraud.Requests, r => Assert.Contains(payment.Id.ToString(), r.Body));
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable)]     // antifraud without Redis
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task Server_Error_Gives_Unknown_After_Every_Attempt(HttpStatusCode status)
    {
        antifraud.Status(status);

        var result = await host.Antifraud.CheckAsync(NewPayment(), Ct);

        Assert.Equal(AntifraudOutcome.Unknown, result.Outcome);
        Assert.False(result.CircuitOpen);                   // one failed check: recovery goes on with the rest
        Assert.Null(result.Score);
        Assert.Equal(host.Attempts, antifraud.Requests.Count);
    }

    [Fact]
    public async Task Hanging_Antifraud_Gives_Unknown_Within_The_Total_Timeout()
    {
        antifraud.Hang();
        var started = Stopwatch.GetTimestamp();

        Assert.Equal(AntifraudOutcome.Unknown, (await host.Antifraud.CheckAsync(NewPayment(), Ct)).Outcome);

        // The budget is tight on purpose: two attempts of 50 ms and the pause take 110 of the 150 ms. On a busy machine
        // (a cold pipeline, containers starting next to the tests) the total timeout may end the check before the retry
        // reaches antifraud. The outcome is Unknown either way, and the retry itself is checked with room to spare in
        // Failed_Attempt_Is_Retried_With_The_Same_Payment. What matters here: a fraction of a second, not the 100 s
        // of HttpClient's own default.
        Assert.InRange(antifraud.Requests.Count, 1, host.Attempts);
        Assert.True(Stopwatch.GetElapsedTime(started) < host.TotalTimeout + TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task Rejected_Check_Fails_At_Once_Without_Retries()
    {
        antifraud.Status(HttpStatusCode.BadRequest);

        // A 400 means our request broke the contract: Unknown would hide the bug behind fail open.
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.Antifraud.CheckAsync(NewPayment(), Ct));
        Assert.Single(antifraud.Requests);
    }

    [Fact]
    public async Task Unknown_Decision_Is_A_Contract_Violation()
    {
        antifraud.Decide("review");

        await Assert.ThrowsAsync<InvalidOperationException>(() => host.Antifraud.CheckAsync(NewPayment(), Ct));
        Assert.Single(antifraud.Requests);
    }

    [Fact]
    public async Task Open_Circuit_Of_Antifraud_Stops_Calling_It_And_Leaves_The_Bank_Alone()
    {
        antifraud.Status(HttpStatusCode.ServiceUnavailable);

        // Each failing check makes Attempts attempts; these checks make enough of them to open the circuit.
        var calls = host.CircuitMinimumThroughput / host.Attempts + 1;
        for (var i = 0; i < calls; i++)
            Assert.Equal(AntifraudOutcome.Unknown, (await host.Antifraud.CheckAsync(NewPayment(), Ct)).Outcome);

        // Open circuit: antifraud answers again, but the check fails fast with Unknown and does not reach it. The
        // decision says so: recovery pauses on it.
        antifraud.Decide("allow");
        var rejected = await host.Antifraud.CheckAsync(NewPayment(), Ct);
        Assert.Equal(AntifraudOutcome.Unknown, rejected.Outcome);
        Assert.True(rejected.CircuitOpen);
        Assert.Equal(host.CircuitMinimumThroughput, antifraud.Requests.Count);

        // The bank is behind a pipeline of its own: its circuit saw none of those failures.
        bank.Decide("approved");
        Assert.Equal(AcquirerOutcome.Approved, (await host.Acquirer.AuthorizeAsync(NewPayment(), Ct)).Outcome);
        Assert.Single(bank.Requests);
    }

    private static Payment NewPayment(long merchantId = 42) =>
        new(Guid.NewGuid(), merchantId, "key", "hash", 150000, "RUB", Fee: null, PaymentStatus.Authorized,
            new DateTimeOffset(2026, 11, 21, 12, 0, 0, TimeSpan.FromHours(3)));
}
