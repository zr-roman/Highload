using System.Diagnostics;
using Contracts;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Payments.Api.Acquiring;
using Payments.Api.Antifraud;
using Payments.Api.Tests.Infrastructure;

namespace Payments.Api.Tests.Recovery;

/// <summary>
/// What recovery does with each stuck payment: the answers of the bank and the antifraud, races and failures, which
/// payments it takes. Recovery runs the same saga as the request, from where it stopped, and never fails open.
/// </summary>
[Collection("Postgres")]
public sealed class PaymentRecoveryTests(PostgresFixture db) : IAsyncLifetime
{
    // Older than RecoveryHost.StaleAfter: recovery takes such a payment on its first pass.
    private static readonly TimeSpan Stale = TimeSpan.FromMinutes(1);

    private readonly FakeBank bank = new();
    private readonly ScriptedAntifraud antifraud = new();
    private readonly FakeTimeProvider time = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => await db.ResetAsync(Ct);

    public ValueTask DisposeAsync()
    {
        Assert.Empty(bank.Unscripted);
        Assert.Empty(antifraud.Unscripted);
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task Approved_And_Allowed_Payment_Is_Captured_With_Fee_And_One_Event_For_The_Ledger()
    {
        var id = await db.InsertPaymentAsync(Stale, cancellationToken: Ct);
        bank.Answer(id, AcquirerOutcome.Approved);
        antifraud.Answer(id, AntifraudOutcome.Allow);

        await using var host = await RecoveryHost.StartAsync(db, bank, time, antifraud: antifraud, cancellationToken: Ct);
        await host.WaitForPassesAsync(1);

        Assert.Equal("captured", await db.GetStatusAsync(id, Ct));
        Assert.Equal(3000L, await db.GetFeeAsync(id, Ct));          // 2 % of 150000 by default FeeOptions
        Assert.Equal(1L, await db.CountOutboxAsync(Topics.PaymentsCaptured, Ct));    // the event Ledger books
        Assert.Equal(1L, await db.CountOutboxAsync(Topics.PaymentsFinalized, Ct));   // and the history's, as in the request
        Assert.Equal(0L, await db.CountPendingAsync(Ct));           // out of the queue of recovery, in the same statement
        Assert.Single(host.Captured.GetMeasurementSnapshot());      // the same metric the endpoint feeds

        var record = Assert.Single(host.Logs.GetSnapshot(), r => r.GetStructuredStateValue("Reason") is not null);
        Assert.Equal("Captured", record.GetStructuredStateValue("Status"));
        Assert.Equal("the antifraud allowed it", record.GetStructuredStateValue("Reason"));
    }

    [Theory]
    [InlineData(AcquirerOutcome.Declined, "the bank declined it")]
    [InlineData(null, "the bank has no decision")]      // 404: the authorization never reached the bank
    public async Task Payment_Declined_Or_Missing_At_The_Bank_Is_Declined(AcquirerOutcome? answer, string reason)
    {
        var id = await db.InsertPaymentAsync(Stale, cancellationToken: Ct);
        bank.Answer(id, answer);

        await using var host = await RecoveryHost.StartAsync(db, bank, time, antifraud: antifraud, cancellationToken: Ct);
        await host.WaitForPassesAsync(1);

        Assert.Equal("declined", await db.GetStatusAsync(id, Ct));
        Assert.Null(await db.GetFeeAsync(id, Ct));
        Assert.Equal(0L, await db.CountOutboxAsync(Topics.PaymentsCaptured, Ct));    // nothing for Ledger to book
        Assert.Equal(1L, await db.CountOutboxAsync(Topics.PaymentsFinalized, Ct));
        Assert.Equal(0L, await db.CountPendingAsync(Ct));
        Assert.Single(host.Declined.GetMeasurementSnapshot());

        var record = Assert.Single(host.Logs.GetSnapshot(), r => r.GetStructuredStateValue("Reason") is not null);
        Assert.Equal("Declined", record.GetStructuredStateValue("Status"));
        Assert.Equal(reason, record.GetStructuredStateValue("Reason"));
        Assert.Equal(0, antifraud.ChecksFor(id));                  // nothing is held, nothing to check
    }

    [Fact]
    public async Task Payment_Voided_At_The_Bank_Is_Recorded_As_Voided_And_The_Batch_Goes_On()
    {
        var voided = await db.InsertPaymentAsync(Stale, cancellationToken: Ct);
        var approved = await db.InsertPaymentAsync(Stale, cancellationToken: Ct);
        bank.Answer(voided, AcquirerOutcome.Voided);
        bank.Answer(approved, AcquirerOutcome.Approved);
        antifraud.Answer(approved, AntifraudOutcome.Allow);

        await using var host = await RecoveryHost.StartAsync(db, bank, time, antifraud: antifraud, cancellationToken: Ct);
        await host.WaitForPassesAsync(1);

        // No money is held: neither captured nor declined, which would hide that the bank had approved it.
        Assert.Equal("voided", await db.GetStatusAsync(voided, Ct));
        Assert.Null(await db.GetFeeAsync(voided, Ct));
        Assert.Equal(0, antifraud.ChecksFor(voided));

        // The bank answered, so it is not taken for down: the rest of the batch is recovered in the same pass.
        Assert.Equal("captured", await db.GetStatusAsync(approved, Ct));
        Assert.Equal(0L, await db.CountPendingAsync(Ct));
        var pass = Assert.Single(host.Passes);
        Assert.Equal(1, Tag(pass, "recovery.captured"));
        Assert.Equal(1, Tag(pass, "recovery.voided"));
        Assert.Equal(0, Tag(pass, "recovery.skipped"));
    }

    [Fact]
    public async Task Each_Payment_Of_A_Batch_Gets_Its_Own_Outcome()
    {
        var approved = await db.InsertPaymentAsync(Stale, cancellationToken: Ct);
        var declined = await db.InsertPaymentAsync(Stale, cancellationToken: Ct);
        var missing = await db.InsertPaymentAsync(Stale, cancellationToken: Ct);
        bank.Answer(approved, AcquirerOutcome.Approved);
        bank.Answer(declined, AcquirerOutcome.Declined);
        bank.NoDecision(missing);

        await using var host = await RecoveryHost.StartAsync(db, bank, time, cancellationToken: Ct);
        await host.WaitForPassesAsync(1);

        Assert.Equal("captured", await db.GetStatusAsync(approved, Ct));
        Assert.Equal("declined", await db.GetStatusAsync(declined, Ct));
        Assert.Equal("declined", await db.GetStatusAsync(missing, Ct));

        var pass = Assert.Single(host.Passes);
        Assert.Equal(ActivityStatusCode.Unset, pass.Status);
        Assert.Equal(3, Tag(pass, "recovery.batch.size"));
        Assert.Equal(1, Tag(pass, "recovery.captured"));
        Assert.Equal(2, Tag(pass, "recovery.declined"));
        Assert.Equal(0, Tag(pass, "recovery.skipped"));
        Assert.Equal(0, Tag(pass, "recovery.previously_resolved"));
    }

    [Fact]
    public async Task Payment_Finished_By_The_Endpoint_During_The_Lookup_Is_Not_Written_Twice()
    {
        var id = await db.InsertPaymentAsync(Stale, cancellationToken: Ct);
        bank.Script(id, async cancellationToken =>
        {
            // The endpoint gets the bank's late answer and captures the payment while recovery is still asking.
            // This stand-in for the endpoint writes no outbox event, so any event would be recovery's.
            await db.SetStatusAsync(id, "captured", cancellationToken);
            return new AcquirerDecision(AcquirerOutcome.Approved);
        });

        await using var host = await RecoveryHost.StartAsync(db, bank, time, cancellationToken: Ct);
        await host.WaitForPassesAsync(1);

        Assert.Equal("captured", await db.GetStatusAsync(id, Ct));
        Assert.Equal(0L, await db.CountOutboxAsync(Ct));            // WHERE status = 'created' found nothing to authorize
        Assert.Empty(host.Captured.GetMeasurementSnapshot());       // and the payment was not counted twice
        Assert.Equal(1, Tag(Assert.Single(host.Passes), "recovery.previously_resolved"));
    }

    [Fact]
    public async Task Failing_Payment_Stays_Created_And_Does_Not_Stop_The_Rest()
    {
        var failing = await db.InsertPaymentAsync(TimeSpan.FromMinutes(3), cancellationToken: Ct);   // the oldest: asked first
        var approved = await db.InsertPaymentAsync(TimeSpan.FromMinutes(2), cancellationToken: Ct);
        var declined = await db.InsertPaymentAsync(Stale, cancellationToken: Ct);
        bank.Fail(failing);
        bank.Answer(approved, AcquirerOutcome.Approved);
        bank.Answer(declined, AcquirerOutcome.Declined);

        await using var host = await RecoveryHost.StartAsync(db, bank, time, cancellationToken: Ct);
        await host.WaitForPassesAsync(1);

        Assert.Equal("created", await db.GetStatusAsync(failing, Ct));    // the next pass retries it
        Assert.Equal("captured", await db.GetStatusAsync(approved, Ct));
        Assert.Equal("declined", await db.GetStatusAsync(declined, Ct));
        Assert.Equal(1, Tag(Assert.Single(host.Passes), "recovery.skipped"));

        var error = Assert.Single(host.Logs.GetSnapshot(), r => r.Level == LogLevel.Error);
        Assert.Equal(failing.ToString(), error.GetStructuredStateValue("PaymentId"));
        Assert.IsType<InvalidOperationException>(error.Exception);
    }

    [Fact]
    public async Task Unavailable_Bank_Stops_The_Pass_And_Recovery_Retries_After_Interval()
    {
        var first = await db.InsertPaymentAsync(TimeSpan.FromMinutes(2), cancellationToken: Ct);
        var second = await db.InsertPaymentAsync(Stale, cancellationToken: Ct);
        bank.AnswerInTurn(first, AcquirerOutcome.Unknown, AcquirerOutcome.Approved);    // down at first, then back
        bank.Answer(second, AcquirerOutcome.Approved);

        await using var host = await RecoveryHost.StartAsync(db, bank, time, cancellationToken: Ct);
        await host.WaitForPassesAsync(1);

        // No answer from the bank: the rest of the pass is skipped without asking it again.
        Assert.Equal(0, bank.LookupsFor(second));
        Assert.Equal("created", await db.GetStatusAsync(first, Ct));
        Assert.Equal("created", await db.GetStatusAsync(second, Ct));

        var pass = Assert.Single(host.Passes);
        Assert.Equal(ActivityStatusCode.Error, pass.Status);
        Assert.Equal(2, Tag(pass, "recovery.skipped"));

        await host.AdvanceUntilAsync(async () => await db.GetStatusAsync(second, Ct) == "captured");

        Assert.Equal("captured", await db.GetStatusAsync(first, Ct));
        Assert.Contains(host.Logs.GetSnapshot(), r => r.Level == LogLevel.Warning && r.Message.StartsWith("Bank unavailable", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Fresh_And_Finished_Payments_Are_Left_Alone()
    {
        var fresh = await db.InsertPaymentAsync(TimeSpan.FromSeconds(1), cancellationToken: Ct);   // the endpoint may still be waiting for the bank
        var captured = await db.InsertPaymentAsync(Stale, "captured", Ct);
        var declined = await db.InsertPaymentAsync(Stale, "declined", Ct);
        var stuck = await db.InsertPaymentAsync(Stale, cancellationToken: Ct);
        bank.Answer(stuck, AcquirerOutcome.Approved);

        await using var host = await RecoveryHost.StartAsync(db, bank, time, cancellationToken: Ct);
        await host.WaitForPassesAsync(1);

        Assert.Equal(stuck, Assert.Single(bank.LookupOrder));              // the bank was asked about this one only
        Assert.Equal("captured", await db.GetStatusAsync(stuck, Ct));
        Assert.Equal("created", await db.GetStatusAsync(fresh, Ct));
        Assert.Equal("captured", await db.GetStatusAsync(captured, Ct));
        Assert.Equal("declined", await db.GetStatusAsync(declined, Ct));
    }

    [Fact]
    public async Task Authorized_Payment_Goes_On_From_The_Antifraud_Without_Asking_The_Bank()
    {
        var authorized = await db.InsertPaymentAsync(Stale, "authorized", Ct);
        antifraud.Answer(authorized, AntifraudOutcome.Allow);

        await using var host = await RecoveryHost.StartAsync(db, bank, time, antifraud: antifraud, cancellationToken: Ct);
        await host.WaitForPassesAsync(1);

        // The approval is in the status already: the bank is not asked again.
        Assert.Equal(0, bank.LookupsFor(authorized));
        Assert.Equal(1, antifraud.ChecksFor(authorized));
        Assert.Equal("captured", await db.GetStatusAsync(authorized, Ct));
        Assert.Equal(1L, await db.CountOutboxAsync(Topics.PaymentsCaptured, Ct));
        Assert.Equal(0L, await db.CountPendingAsync(Ct));
    }

    [Fact]
    public async Task Authorized_Payment_Denied_By_The_Antifraud_Is_Voided_At_The_Bank()
    {
        var authorized = await db.InsertPaymentAsync(Stale, "authorized", Ct);
        antifraud.Answer(authorized, AntifraudOutcome.Deny);
        bank.AnswerVoid(authorized, VoidOutcome.Voided);

        await using var host = await RecoveryHost.StartAsync(db, bank, time, antifraud: antifraud, cancellationToken: Ct);
        await host.WaitForPassesAsync(1);

        Assert.Equal(1, bank.VoidsFor(authorized));
        Assert.Equal("voided", await db.GetStatusAsync(authorized, Ct));
        Assert.Equal(0L, await db.CountOutboxAsync(Topics.PaymentsCaptured, Ct));
        Assert.Equal(1L, await db.CountOutboxAsync(Topics.PaymentsFinalized, Ct));
        Assert.Equal(0L, await db.CountPendingAsync(Ct));
        Assert.Equal(1, Tag(Assert.Single(host.Passes), "recovery.voided"));

        // The same metrics as in the request, told apart by the path.
        Assert.Equal(("deny", "recovery"), OutcomeAndPath(Assert.Single(host.AntifraudDecisions.GetMeasurementSnapshot())));
        var voidedMeasurement = Assert.Single(host.Voided.GetMeasurementSnapshot());
        Assert.Equal("antifraud_deny", voidedMeasurement.Tags["reason"]);
        Assert.Equal("recovery", voidedMeasurement.Tags["path"]);
    }

    [Fact]
    public async Task Approved_Payment_Without_An_Antifraud_Decision_Waits_In_Authorized_And_Is_Never_Captured_Unchecked()
    {
        // Both well below RecoveryHost.FailOpenBelowAmount: the request path would let them through unchecked.
        var first = await db.InsertPaymentAsync(TimeSpan.FromMinutes(2), cancellationToken: Ct);
        var second = await db.InsertPaymentAsync(Stale, cancellationToken: Ct);
        bank.Answer(first, AcquirerOutcome.Approved);
        bank.Answer(second, AcquirerOutcome.Approved);
        antifraud.AnswerInTurn(first, AntifraudOutcome.Unknown, AntifraudOutcome.Allow);   // no decision at first, then one
        antifraud.Answer(second, AntifraudOutcome.Allow);

        await using var host = await RecoveryHost.StartAsync(db, bank, time, antifraud: antifraud, cancellationToken: Ct);
        await host.WaitForPassesAsync(1);

        // The bank approved, so the payment is authorized, and it waits there for a decision: no fail open. One check
        // without a decision holds back that payment alone: the pass goes on with the next one.
        Assert.Equal("authorized", await db.GetStatusAsync(first, Ct));
        Assert.Equal("captured", await db.GetStatusAsync(second, Ct));
        Assert.Equal(1L, await db.CountOutboxAsync(Topics.PaymentsCaptured, Ct));    // the event of the second one only
        var pass = Assert.Single(host.Passes);
        Assert.NotEqual(ActivityStatusCode.Error, pass.Status);
        Assert.Equal(1, Tag(pass, "recovery.skipped"));
        Assert.Equal(1, Tag(pass, "recovery.captured"));
        Assert.Empty(host.FailOpen.GetMeasurementSnapshot());
        var left = Assert.Single(host.LeftInProgress.GetMeasurementSnapshot());
        Assert.Equal("antifraud", left.Tags["waits_for"]);
        Assert.Equal("recovery", left.Tags["path"]);

        // The next pass, after Interval, goes on from 'authorized': the antifraud only, not the bank again.
        await host.AdvanceUntilAsync(async () => await db.GetStatusAsync(first, Ct) == "captured");

        Assert.Equal(1, bank.LookupsFor(first));
        Assert.Equal(2, antifraud.ChecksFor(first));
        Assert.Equal(1, antifraud.ChecksFor(second));
        Assert.DoesNotContain(host.Logs.GetSnapshot(), r => r.Message.StartsWith("Antifraud unavailable", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Open_Circuit_Of_The_Antifraud_Stops_The_Pass_And_Recovery_Retries_After_Interval()
    {
        var first = await db.InsertPaymentAsync(TimeSpan.FromMinutes(2), "authorized", Ct);
        var second = await db.InsertPaymentAsync(Stale, "authorized", Ct);
        antifraud.AnswerInTurn(first, AntifraudDecision.Unavailable, ScriptedAntifraud.Decision(AntifraudOutcome.Allow));  // down, then back
        antifraud.Answer(second, AntifraudOutcome.Allow);

        await using var host = await RecoveryHost.StartAsync(db, bank, time, antifraud: antifraud, cancellationToken: Ct);
        await host.WaitForPassesAsync(1);

        // The circuit is open, so the antifraud is down: the rest of the pass is skipped without asking it again.
        Assert.Equal(0, antifraud.ChecksFor(second));
        Assert.Equal("authorized", await db.GetStatusAsync(first, Ct));
        Assert.Equal("authorized", await db.GetStatusAsync(second, Ct));

        var pass = Assert.Single(host.Passes);
        Assert.Equal(ActivityStatusCode.Error, pass.Status);
        Assert.Equal(2, Tag(pass, "recovery.skipped"));

        await host.AdvanceUntilAsync(async () => await db.GetStatusAsync(second, Ct) == "captured");

        Assert.Equal("captured", await db.GetStatusAsync(first, Ct));
        Assert.Equal(2, antifraud.ChecksFor(first));
        Assert.Contains(host.Logs.GetSnapshot(), r => r.Level == LogLevel.Warning && r.Message.StartsWith("Antifraud unavailable", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Age_Of_The_Oldest_Authorized_Payment_Grows_Until_It_Is_Finished()
    {
        // The gauge counts the age by the clock of the service, here the fake one: it starts at the real time.
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var authorized = await db.InsertPaymentAsync(TimeSpan.FromMinutes(2), "authorized", Ct);
        antifraud.AnswerInTurn(authorized, AntifraudOutcome.Unknown, AntifraudOutcome.Allow);

        await using var host = await RecoveryHost.StartAsync(db, bank, clock, antifraud: antifraud, cancellationToken: Ct);
        await host.WaitForPassesAsync(1);

        // Recovery looks at its queue after the pass, so after its span has ended: wait for the gauge itself.
        await Eventually.WaitUntilAsync(() => OldestAges(host)["authorized"] > 0);

        // The antifraud is silent: the payment waits, two minutes old. No payment waits in 'created'.
        var ages = OldestAges(host);
        Assert.InRange(ages["authorized"], 119, 150);
        Assert.Equal(0, ages["created"]);

        // Captured on a later pass, and the pass after it reports no payment waiting.
        await host.AdvanceUntilAsync(async () => await db.GetStatusAsync(authorized, Ct) == "captured" && OldestAges(host)["authorized"] == 0);
    }

    [Fact]
    public async Task Oldest_Payments_Are_Recovered_First()
    {
        // Inserted out of order, so the order of the lookups can only come from created_at.
        var newest = await db.InsertPaymentAsync(TimeSpan.FromMinutes(1), cancellationToken: Ct);
        var oldest = await db.InsertPaymentAsync(TimeSpan.FromMinutes(3), cancellationToken: Ct);
        var middle = await db.InsertPaymentAsync(TimeSpan.FromMinutes(2), cancellationToken: Ct);
        foreach (var id in new[] { newest, oldest, middle })
            bank.Answer(id, AcquirerOutcome.Approved);

        // Two per batch: the first pass takes the two oldest, the second one the newest.
        await using var host = await RecoveryHost.StartAsync(db, bank, time, batchSize: 2, cancellationToken: Ct);
        await host.WaitForPassesAsync(2);

        Assert.Equal(new[] { oldest, middle, newest }, bank.LookupOrder);
    }

    private static int Tag(Activity span, string name) => (int)span.GetTagItem(name)!;

    private static (object?, object?) OutcomeAndPath(CollectedMeasurement<long> measurement) =>
        (measurement.Tags["outcome"], measurement.Tags["path"]);

    // A gauge is read when it is asked for: the last measurement of each status is its value now.
    private static Dictionary<string, double> OldestAges(RecoveryHost host)
    {
        host.OldestInProgress.RecordObservableInstruments();
        return host.OldestInProgress.GetMeasurementSnapshot()
            .GroupBy(m => (string)m.Tags["status"]!)
            .ToDictionary(g => g.Key, g => g.Last().Value);
    }
}
