using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Payments.Api.Acquiring;
using Payments.Api.Payments;
using Payments.Api.Tests.Infrastructure;

namespace Payments.Api.Tests.Acquiring;

/// <summary>
/// HttpAcquirer with the real resilience pipeline over a stub bank, without the network: what each answer
/// of the bank becomes, and how retries, timeouts and the circuit breaker treat failures. Time is real here,
/// which is why the attempt timeout is shortened; the expected counts come from the service's settings.
/// </summary>
public sealed class HttpAcquirerTests : IDisposable
{
    private readonly StubBankHandler bank = new();
    private readonly AcquirerHost host;

    public HttpAcquirerTests() => host = new AcquirerHost(bank);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => host.Dispose();

    [Theory]
    [InlineData("approved", AcquirerOutcome.Approved)]
    [InlineData("declined", AcquirerOutcome.Declined)]
    public async Task Bank_Decision_Becomes_The_Outcome(string status, AcquirerOutcome outcome)
    {
        bank.Decide(status, authorizationCode: "A-123");

        var decision = await host.Acquirer.AuthorizeAsync(NewPayment(), Ct);

        Assert.Equal(new AcquirerDecision(outcome, "A-123"), decision);
        Assert.Single(bank.Requests);
    }

    [Fact]
    public async Task Authorization_Is_A_Post_With_The_Payment()
    {
        var payment = NewPayment();
        bank.Decide("approved");

        await host.Acquirer.AuthorizeAsync(payment, Ct);

        // The contract with the Go emulator: the path and the camelCase fields.
        var request = Assert.Single(bank.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/v1/authorizations", request.Path);

        using var body = JsonDocument.Parse(request.Body!);
        Assert.Equal(payment.Id, body.RootElement.GetProperty("paymentId").GetGuid());
        Assert.Equal(payment.MerchantId, body.RootElement.GetProperty("merchantId").GetInt64());
        Assert.Equal(payment.Amount, body.RootElement.GetProperty("amount").GetInt64());
        Assert.Equal(payment.Currency, body.RootElement.GetProperty("currency").GetString());
    }

    [Fact]
    public async Task Bank_Error_Is_Retried_With_The_Same_Payment()
    {
        var payment = NewPayment();
        bank.Status(HttpStatusCode.InternalServerError).Decide("approved");

        var decision = await host.Acquirer.AuthorizeAsync(payment, Ct);

        Assert.Equal(AcquirerOutcome.Approved, decision.Outcome);
        Assert.Equal(2, bank.Requests.Count);

        // The retry repeats the same authorization. Safe only because the bank is idempotent by paymentId.
        Assert.All(bank.Requests, request => Assert.Equal(payment.Id, PaymentIdOf(request)));
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task Bank_That_Keeps_Failing_Gives_Unknown_After_Every_Attempt(HttpStatusCode status)
    {
        bank.Status(status);

        var decision = await host.Acquirer.AuthorizeAsync(NewPayment(), Ct);

        Assert.Equal(AcquirerOutcome.Unknown, decision.Outcome);
        Assert.Equal(host.Attempts, bank.Requests.Count);
    }

    [Fact]
    public async Task Unreachable_Bank_Gives_Unknown_After_Every_Attempt()
    {
        bank.Unreachable();

        var decision = await host.Acquirer.AuthorizeAsync(NewPayment(), Ct);

        Assert.Equal(AcquirerOutcome.Unknown, decision.Outcome);
        Assert.Equal(host.Attempts, bank.Requests.Count);
    }

    [Fact]
    public async Task Hanging_Bank_Gives_Unknown_Before_The_Total_Timeout()
    {
        bank.Hang();
        var started = Stopwatch.GetTimestamp();

        var decision = await host.Acquirer.AuthorizeAsync(NewPayment(), Ct);

        // Every attempt ran into the attempt timeout and was retried; the whole call stayed within the total timeout.
        Assert.Equal(AcquirerOutcome.Unknown, decision.Outcome);
        Assert.Equal(host.Attempts, bank.Requests.Count);
        Assert.True(Stopwatch.GetElapsedTime(started) < host.TotalTimeout);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Conflict)]
    public async Task Rejected_Authorization_Fails_At_Once_Without_Retries(HttpStatusCode status)
    {
        bank.Status(status);

        // A 4xx means our request is wrong: a retry would get the same answer, and "unknown" would be a lie.
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.Acquirer.AuthorizeAsync(NewPayment(), Ct));
        Assert.Single(bank.Requests);
    }

    [Fact]
    public async Task Unknown_Status_From_The_Bank_Is_A_Contract_Violation()
    {
        bank.Decide("pending");

        await Assert.ThrowsAsync<InvalidOperationException>(() => host.Acquirer.AuthorizeAsync(NewPayment(), Ct));
        Assert.Single(bank.Requests);
    }

    [Fact]
    public async Task Caller_Cancellation_Is_Not_An_Unknown_Outcome()
    {
        bank.Hang();
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(Ct);

        var call = host.Acquirer.AuthorizeAsync(NewPayment(), caller.Token);
        await bank.FirstRequest.WaitAsync(Ct);
        caller.Cancel();                                            // well before the attempt timeout

        // The caller went away: the call is cancelled, not answered with Unknown, and nothing is retried.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);
        Assert.Single(bank.Requests);
    }

    [Fact]
    public async Task Circuit_Opens_After_Repeated_Failures_And_Stops_Calling_The_Bank()
    {
        bank.Status(HttpStatusCode.InternalServerError);

        // Each failing call makes Attempts attempts; these calls make enough of them to open the circuit.
        var calls = host.CircuitMinimumThroughput / host.Attempts + 1;
        for (var i = 0; i < calls; i++)
            Assert.Equal(AcquirerOutcome.Unknown, (await host.Acquirer.AuthorizeAsync(NewPayment(), Ct)).Outcome);

        // Open circuit: the call fails fast with Unknown, and the bank does not see it.
        Assert.Equal(AcquirerOutcome.Unknown, (await host.Acquirer.AuthorizeAsync(NewPayment(), Ct)).Outcome);
        Assert.Equal(host.CircuitMinimumThroughput, bank.Requests.Count);
    }

    [Fact]
    public async Task Decision_Lookup_Returns_The_Stored_Decision()
    {
        var paymentId = Guid.NewGuid();
        bank.Decide("declined", authorizationCode: "A-7");

        var decision = await host.Acquirer.FindDecisionAsync(paymentId, Ct);

        Assert.Equal(new AcquirerDecision(AcquirerOutcome.Declined, "A-7"), decision);
        var request = Assert.Single(bank.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal($"/v1/authorizations/{paymentId}", request.Path);
    }

    [Fact]
    public async Task Missing_Decision_Is_Null_Without_Retries()
    {
        bank.Status(HttpStatusCode.NotFound);

        // 404 is an answer, not a failure: the bank never got the authorization, and recovery declines the payment.
        Assert.Null(await host.Acquirer.FindDecisionAsync(Guid.NewGuid(), Ct));
        Assert.Single(bank.Requests);
    }

    [Fact]
    public async Task Failing_Lookup_Gives_Unknown_After_Every_Attempt()
    {
        bank.Status(HttpStatusCode.InternalServerError);

        var decision = await host.Acquirer.FindDecisionAsync(Guid.NewGuid(), Ct);

        Assert.Equal(AcquirerOutcome.Unknown, decision?.Outcome);
        Assert.Equal(host.Attempts, bank.Requests.Count);
    }

    private static Payment NewPayment() =>
        new(Guid.NewGuid(), 42, "key", "hash", 150000, "RUB", Fee: null, PaymentStatus.Created, DateTimeOffset.UtcNow);

    private static Guid PaymentIdOf(BankRequest request)
    {
        using var body = JsonDocument.Parse(request.Body!);
        return body.RootElement.GetProperty("paymentId").GetGuid();
    }
}
