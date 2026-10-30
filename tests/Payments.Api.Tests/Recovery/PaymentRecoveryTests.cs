using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Payments.Api.Acquiring;
using Payments.Api.Tests.Infrastructure;

namespace Payments.Api.Tests.Recovery;

/// <summary>What recovery does with each stuck payment: the bank's answers, races and failures, which payments it takes.</summary>
[Collection("Postgres")]
public sealed class PaymentRecoveryTests(PostgresFixture db) : IAsyncLifetime
{
    // Older than RecoveryHost.StaleAfter: recovery takes such a payment on its first pass.
    private static readonly TimeSpan Stale = TimeSpan.FromMinutes(1);

    private readonly FakeBank bank = new();
    private readonly FakeTimeProvider time = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => await db.ResetAsync(Ct);

    public ValueTask DisposeAsync()
    {
        Assert.Empty(bank.Unscripted);
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task Approved_Payment_Is_Captured_With_Fee_And_One_Outbox_Event()
    {
        var id = await db.InsertPaymentAsync(Stale, cancellationToken: Ct);
        bank.Answer(id, AcquirerOutcome.Approved);

        await using var host = await RecoveryHost.StartAsync(db, bank, time, cancellationToken: Ct);
        await host.WaitForPassesAsync(1);

        Assert.Equal("captured", await db.GetStatusAsync(id, Ct));
        Assert.Equal(3000L, await db.GetFeeAsync(id, Ct));          // 2 % of 150000 by default FeeOptions
        Assert.Equal(1L, await db.CountOutboxAsync(Ct));            // the event Ledger books
        Assert.Single(host.Captured.GetMeasurementSnapshot());      // the same metric the endpoint feeds

        var record = Assert.Single(host.Logs.GetSnapshot(), r => r.GetStructuredStateValue("Reason") is not null);
        Assert.Equal("Captured", record.GetStructuredStateValue("Status"));
        Assert.Equal("the bank approved it", record.GetStructuredStateValue("Reason"));
    }

    [Theory]
    [InlineData(AcquirerOutcome.Declined, "the bank declined it")]
    [InlineData(null, "the bank has no decision")]      // 404: the authorization never reached the bank
    public async Task Payment_Declined_Or_Missing_At_The_Bank_Is_Declined(AcquirerOutcome? answer, string reason)
    {
        var id = await db.InsertPaymentAsync(Stale, cancellationToken: Ct);
        bank.Answer(id, answer);

        await using var host = await RecoveryHost.StartAsync(db, bank, time, cancellationToken: Ct);
        await host.WaitForPassesAsync(1);

        Assert.Equal("declined", await db.GetStatusAsync(id, Ct));
        Assert.Null(await db.GetFeeAsync(id, Ct));
        Assert.Equal(0L, await db.CountOutboxAsync(Ct));            // nothing for Ledger to book
        Assert.Single(host.Declined.GetMeasurementSnapshot());

        var record = Assert.Single(host.Logs.GetSnapshot(), r => r.GetStructuredStateValue("Reason") is not null);
        Assert.Equal("Declined", record.GetStructuredStateValue("Status"));
        Assert.Equal(reason, record.GetStructuredStateValue("Reason"));
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
        Assert.Equal(0L, await db.CountOutboxAsync(Ct));            // WHERE status = 'created' found nothing to update
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
}
