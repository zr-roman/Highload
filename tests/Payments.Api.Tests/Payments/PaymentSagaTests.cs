using System.Diagnostics.Metrics;
using Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
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
/// give leads to its own status, every final status gets its PaymentFinalized, and only a captured payment gets an
/// event for the ledger as well. Every outcome is also counted
/// in the metrics of the saga, with path = request.
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
    private MetricCollector<long> decisions = null!;
    private MetricCollector<long> failOpen = null!;
    private MetricCollector<long> voided = null!;
    private MetricCollector<long> leftInProgress = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => await db.ResetAsync(Ct);

    public async ValueTask DisposeAsync()
    {
        Assert.Empty(bank.Unscripted);
        Assert.Empty(antifraud.Unscripted);
        if (services is not null)
        {
            decisions.Dispose();
            failOpen.Dispose();
            voided.Dispose();
            leftInProgress.Dispose();
            await services.DisposeAsync();
        }
    }

    [Fact]
    public async Task Allowed_Payment_Is_Captured_With_Fee_And_One_Event_For_The_Ledger()
    {
        var payment = await NewPaymentAsync();
        bank.AnswerAuthorization(payment.Id, AcquirerOutcome.Approved);
        antifraud.Answer(payment.Id, AntifraudOutcome.Allow);

        var result = await Saga().RunAsync(payment, Ct);

        Assert.Equal(PaymentStatus.Captured, result.Status);
        Assert.Equal(3000L, result.Fee);                            // 2 % of 150000, what the response shows
        Assert.Equal("captured", await db.GetStatusAsync(payment.Id, Ct));
        Assert.Equal(1L, await db.CountOutboxAsync(Topics.PaymentsCaptured, Ct));    // the event the ledger books
        Assert.Equal(1L, await db.CountOutboxAsync(Topics.PaymentsFinalized, Ct));   // and the one of the history
        Assert.Equal(0L, await db.CountPendingAsync(Ct));
        Assert.Equal(0, bank.VoidsFor(payment.Id));

        AssertCounted(decisions, ("outcome", "allow"), ("path", "request"));
        Assert.Empty(voided.GetMeasurementSnapshot());
        Assert.Empty(failOpen.GetMeasurementSnapshot());
    }

    [Fact]
    public async Task Denied_Payment_Is_Voided_At_The_Bank_Without_An_Event_For_The_Ledger()
    {
        var payment = await NewPaymentAsync();
        bank.AnswerAuthorization(payment.Id, AcquirerOutcome.Approved);
        antifraud.Answer(payment.Id, AntifraudOutcome.Deny);
        bank.AnswerVoid(payment.Id, VoidOutcome.Voided);

        var result = await Saga().RunAsync(payment, Ct);

        Assert.Equal(PaymentStatus.Voided, result.Status);
        Assert.Equal(1, bank.VoidsFor(payment.Id));                 // the compensation: the hold is released
        Assert.Equal("voided", await db.GetStatusAsync(payment.Id, Ct));
        Assert.Equal(0L, await db.CountOutboxAsync(Topics.PaymentsCaptured, Ct));    // no money moved: nothing for the ledger
        Assert.Equal(1L, await db.CountOutboxAsync(Topics.PaymentsFinalized, Ct));   // the history has it all the same
        Assert.Equal(0L, await db.CountPendingAsync(Ct));
        Assert.Null(await db.GetFeeAsync(payment.Id, Ct));

        AssertCounted(decisions, ("outcome", "deny"), ("path", "request"));
        AssertCounted(voided, ("reason", "antifraud_deny"), ("path", "request"));
    }

    [Theory]
    [InlineData(VoidOutcome.NotFound, "void_not_found")]    // the emulator lost the decision on a restart
    [InlineData(VoidOutcome.Conflict, "void_conflict")]     // the bank had declined the payment
    public async Task Void_That_Finds_No_Hold_Is_Recorded_As_Voided_With_A_Warning(VoidOutcome answer, string reason)
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
        AssertCounted(voided, ("reason", reason), ("path", "request"));    // apart from a plain void on the dashboard
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
        AssertCounted(leftInProgress, ("waits_for", "bank"), ("path", "request"));
        Assert.Empty(voided.GetMeasurementSnapshot());
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
        Assert.Equal(expected == PaymentStatus.Captured ? 1L : 0L, await db.CountOutboxAsync(Topics.PaymentsCaptured, Ct));
        Assert.Equal(expected == PaymentStatus.Captured ? 0L : 1L, await db.CountPendingAsync(Ct));
        Assert.Equal(0, bank.VoidsFor(payment.Id));                 // no decision is not a deny

        AssertCounted(decisions, ("outcome", "unknown"), ("path", "request"));
        if (expected == PaymentStatus.Captured)
        {
            Assert.Single(failOpen.GetMeasurementSnapshot());       // the risk taken on
            Assert.Empty(leftInProgress.GetMeasurementSnapshot());
        }
        else
        {
            Assert.Empty(failOpen.GetMeasurementSnapshot());
            AssertCounted(leftInProgress, ("waits_for", "antifraud"), ("path", "request"));
        }
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
        AssertCounted(leftInProgress, ("waits_for", "bank"), ("path", "request"));
        Assert.Empty(decisions.GetMeasurementSnapshot());
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
        AssertCounted(voided, ("reason", "bank_voided"), ("path", "request"));
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

        var meterFactory = services.GetRequiredService<IMeterFactory>();
        decisions = new MetricCollector<long>(meterFactory, PaymentsMetrics.MeterName, "payments.antifraud.decisions");
        failOpen = new MetricCollector<long>(meterFactory, PaymentsMetrics.MeterName, "payments.antifraud.fail_open");
        voided = new MetricCollector<long>(meterFactory, PaymentsMetrics.MeterName, "payments.voided");
        leftInProgress = new MetricCollector<long>(meterFactory, PaymentsMetrics.MeterName, "payments.left_in_progress");

        return services.GetRequiredService<PaymentSaga>();
    }

    // One measurement of 1, with exactly these tags.
    private static void AssertCounted(MetricCollector<long> collector, params (string Name, string Value)[] tags)
    {
        var measurement = Assert.Single(collector.GetMeasurementSnapshot());
        Assert.Equal(1, measurement.Value);
        Assert.Equal(
            tags.ToDictionary(t => t.Name, t => (object?)t.Value),
            measurement.Tags.ToDictionary(t => t.Key, t => t.Value));
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
