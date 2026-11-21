using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Payments.Api.Acquiring;
using Payments.Api.Antifraud;
using Payments.Api.Payments;
using Payments.Api.RateLimiting;
using Payments.Api.Telemetry;
using Payments.Api.Tests.Infrastructure;

namespace Payments.Api.Tests.Payments;

/// <summary>
/// The saga of the request path on a real Postgres, with the bank and the antifraud scripted: every answer they can
/// give leads to its own status, and only a captured payment gets an event for the ledger.
/// </summary>
[Collection("Postgres")]
public sealed class PaymentSagaTests(PostgresFixture db) : IAsyncLifetime
{
    // The threshold of the stand (appsettings.json): 500 roubles.
    private const long FailOpenBelowAmount = 50_000;

    private readonly FakeBank bank = new();
    private readonly ScriptedAntifraud antifraud = new();
    private readonly PaymentStore store = new(db.DataSource);
    private ServiceProvider? services;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => await db.ResetAsync(Ct);

    public async ValueTask DisposeAsync()
    {
        Assert.Empty(bank.Unscripted);
        Assert.Empty(antifraud.Unscripted);
        if (services is not null)
            await services.DisposeAsync();
    }

    [Fact]
    public async Task Allowed_Payment_Is_Captured_With_Fee_And_One_Outbox_Event()
    {
        var payment = await NewPaymentAsync();
        bank.AnswerAuthorization(payment.Id, AcquirerOutcome.Approved);
        antifraud.Answer(payment.Id, AntifraudOutcome.Allow);

        var result = await Saga().RunAsync(payment, Ct);

        Assert.Equal(PaymentStatus.Captured, result.Status);
        Assert.Equal(3000L, result.Fee);                            // 2 % of 150000, what the response shows
        Assert.Equal("captured", await db.GetStatusAsync(payment.Id, Ct));
        Assert.Equal(1L, await db.CountOutboxAsync(Ct));            // the event the ledger books
        Assert.Equal(0L, await db.CountPendingAsync(Ct));
        Assert.Equal(0, bank.VoidsFor(payment.Id));
    }

    [Fact]
    public async Task Denied_Payment_Is_Voided_At_The_Bank_Without_An_Outbox_Event()
    {
        var payment = await NewPaymentAsync();
        bank.AnswerAuthorization(payment.Id, AcquirerOutcome.Approved);
        antifraud.Answer(payment.Id, AntifraudOutcome.Deny);
        bank.AnswerVoid(payment.Id, VoidOutcome.Voided);

        var result = await Saga().RunAsync(payment, Ct);

        Assert.Equal(PaymentStatus.Voided, result.Status);
        Assert.Equal(1, bank.VoidsFor(payment.Id));                 // the compensation: the hold is released
        Assert.Equal("voided", await db.GetStatusAsync(payment.Id, Ct));
        Assert.Equal(0L, await db.CountOutboxAsync(Ct));            // no money moved: nothing for the ledger
        Assert.Equal(0L, await db.CountPendingAsync(Ct));
        Assert.Null(await db.GetFeeAsync(payment.Id, Ct));
    }

    [Theory]
    [InlineData(VoidOutcome.NotFound)]      // the emulator lost the decision on a restart
    [InlineData(VoidOutcome.Conflict)]      // the bank had declined the payment
    public async Task Void_That_Finds_No_Hold_Is_Recorded_As_Voided_With_A_Warning(VoidOutcome answer)
    {
        var payment = await NewPaymentAsync();
        bank.AnswerAuthorization(payment.Id, AcquirerOutcome.Approved);
        antifraud.Answer(payment.Id, AntifraudOutcome.Deny);
        bank.AnswerVoid(payment.Id, answer);

        var result = await Saga().RunAsync(payment, Ct);

        // The bank holds no money either way, and a repeated void would answer the same.
        Assert.Equal(PaymentStatus.Voided, result.Status);
        Assert.Equal("voided", await db.GetStatusAsync(payment.Id, Ct));
        var warning = Assert.Single(Logs(), r => r.Level == LogLevel.Warning);
        Assert.Equal(answer.ToString(), warning.GetStructuredStateValue("VoidOutcome"));
    }

    [Fact]
    public async Task Void_Without_An_Answer_Leaves_The_Payment_Authorized()
    {
        var payment = await NewPaymentAsync();
        bank.AnswerAuthorization(payment.Id, AcquirerOutcome.Approved);
        antifraud.Answer(payment.Id, AntifraudOutcome.Deny);
        bank.AnswerVoid(payment.Id, VoidOutcome.Unknown);

        var result = await Saga().RunAsync(payment, Ct);

        // The void may or may not have happened: recovery voids again, which the bank takes as a repeat.
        Assert.Equal(PaymentStatus.Authorized, result.Status);
        Assert.Equal("authorized", await db.GetStatusAsync(payment.Id, Ct));
        Assert.Equal(1L, await db.CountPendingAsync(Ct));
        Assert.Equal(0L, await db.CountOutboxAsync(Ct));
    }

    [Theory]
    [InlineData(49_999, FailOpenBelowAmount, PaymentStatus.Captured)]     // below the threshold: fail open
    [InlineData(50_000, FailOpenBelowAmount, PaymentStatus.Authorized)]   // at it: waits for a real check
    [InlineData(1_000, 0, PaymentStatus.Authorized)]                      // 0: fail open off
    public async Task Payment_Without_An_Antifraud_Decision_Is_Captured_Only_Below_The_Threshold(
        long amount, long failOpenBelowAmount, PaymentStatus expected)
    {
        var payment = await NewPaymentAsync(amount);
        bank.AnswerAuthorization(payment.Id, AcquirerOutcome.Approved);
        antifraud.Answer(payment.Id, AntifraudOutcome.Unknown);

        var result = await Saga(failOpenBelowAmount).RunAsync(payment, Ct);

        Assert.Equal(expected, result.Status);
        Assert.Equal(expected == PaymentStatus.Captured ? 1L : 0L, await db.CountOutboxAsync(Ct));
        Assert.Equal(expected == PaymentStatus.Captured ? 0L : 1L, await db.CountPendingAsync(Ct));
        Assert.Equal(0, bank.VoidsFor(payment.Id));                 // no decision is not a deny
    }

    [Fact]
    public async Task Payment_Declined_By_The_Bank_Is_Not_Checked()
    {
        var payment = await NewPaymentAsync();
        bank.AnswerAuthorization(payment.Id, AcquirerOutcome.Declined);

        var result = await Saga().RunAsync(payment, Ct);

        Assert.Equal(PaymentStatus.Declined, result.Status);
        Assert.Equal("declined", await db.GetStatusAsync(payment.Id, Ct));
        Assert.Equal(0, antifraud.ChecksFor(payment.Id));           // nothing is held, nothing to check
    }

    [Fact]
    public async Task Payment_Without_An_Answer_From_The_Bank_Stays_Created_And_Unchecked()
    {
        var payment = await NewPaymentAsync();
        bank.AnswerAuthorization(payment.Id, AcquirerOutcome.Unknown);

        var result = await Saga().RunAsync(payment, Ct);

        // The bank may have approved: recovery asks it later and goes on from its answer.
        Assert.Equal(PaymentStatus.Created, result.Status);
        Assert.Equal("created", await db.GetStatusAsync(payment.Id, Ct));
        Assert.Equal(1L, await db.CountPendingAsync(Ct));
        Assert.Equal(0, antifraud.ChecksFor(payment.Id));
    }

    [Fact]
    public async Task Payment_The_Bank_Reports_As_Voided_Is_Recorded_As_Voided()
    {
        var payment = await NewPaymentAsync();
        bank.AnswerAuthorization(payment.Id, AcquirerOutcome.Voided);

        var result = await Saga().RunAsync(payment, Ct);

        Assert.Equal(PaymentStatus.Voided, result.Status);
        Assert.Equal("voided", await db.GetStatusAsync(payment.Id, Ct));
        Assert.Equal(0L, await db.CountPendingAsync(Ct));
        Assert.Equal(0, antifraud.ChecksFor(payment.Id));
    }

    [Fact]
    public async Task Payment_Settled_Elsewhere_During_The_Check_Is_Answered_As_Recorded()
    {
        var payment = await NewPaymentAsync();
        bank.AnswerAuthorization(payment.Id, AcquirerOutcome.Approved);

        // Recovery took the payment for stuck and captured it while the antifraud was still answering this saga.
        antifraud.Script(payment.Id, async () =>
        {
            await db.SetStatusAsync(payment.Id, "captured", Ct);
            return new AntifraudDecision(AntifraudOutcome.Allow);
        });

        var result = await Saga().RunAsync(payment, Ct);

        Assert.Equal(PaymentStatus.Captured, result.Status);        // what the database holds
        Assert.Equal(0L, await db.CountOutboxAsync(Ct));            // the saga wrote no second event
    }

    // The saga as Program.cs registers it, with the bank and the antifraud scripted.
    private PaymentSaga Saga(long failOpenBelowAmount = FailOpenBelowAmount)
    {
        var collection = new ServiceCollection()
            .AddMetrics()
            .AddFakeLogging()
            .AddSingleton(db.DataSource)
            .AddSingleton(TimeProvider.System)
            .AddSingleton<IAcquirer>(bank)
            .AddSingleton<IAntifraud>(antifraud)
            .AddSingleton<PaymentStore>()
            .AddSingleton<MerchantQuotas>()
            .AddSingleton<PaymentCompletion>()
            .AddSingleton<PaymentsMetrics>()
            .AddTransient<PaymentSaga>();

        collection.AddOptions<FeeOptions>();
        collection.AddOptions<MerchantRateLimitOptions>();
        collection.Configure<AntifraudOptions>(options => options.FailOpenBelowAmount = failOpenBelowAmount);

        services = collection.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        return services.GetRequiredService<PaymentSaga>();
    }

    private IReadOnlyList<FakeLogRecord> Logs() => services!.GetFakeLogCollector().GetSnapshot();

    // Written as the endpoint writes it, before the saga starts.
    private async Task<Payment> NewPaymentAsync(long amount = 150_000)
    {
        var payment = new Payment(
            Id: Guid.CreateVersion7(),
            MerchantId: 42,
            IdempotencyKey: Guid.NewGuid().ToString(),
            RequestHash: "test",
            Amount: amount,
            Currency: "RUB",
            Fee: null,
            Status: PaymentStatus.Created,
            CreatedAt: DateTimeOffset.UtcNow);

        Assert.True(await store.TryInsertAsync(payment, Ct));
        return payment;
    }
}
