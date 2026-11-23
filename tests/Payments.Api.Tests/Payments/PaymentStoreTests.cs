using System.Text.Json;
using Contracts;
using Payments.Api.Payments;
using Payments.Api.Tests.Infrastructure;

namespace Payments.Api.Tests.Payments;

/// <summary>
/// The transitions of the saga in PaymentStore, on a real Postgres: each one moves a payment only out of its own
/// source statuses, and the queue of recovery and the outbox change in the same statement. Every final status writes
/// one PaymentFinalized, and only a capture writes a PaymentCaptured for the ledger as well.
/// </summary>
[Collection("Postgres")]
public sealed class PaymentStoreTests(PostgresFixture db) : IAsyncLifetime
{
    // The age of a payment plays no part here, the helper only needs one.
    private static readonly TimeSpan Age = TimeSpan.FromSeconds(1);

    private readonly PaymentStore store = new(db.DataSource);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => await db.ResetAsync(Ct);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task Authorized_Payment_Stays_In_The_Queue_Of_Recovery()
    {
        var id = await db.InsertPaymentAsync(Age, "created", Ct);

        Assert.True(await store.MarkAuthorizedAsync(id, Ct));

        // Not final: if the process dies before the antifraud decides, recovery finds the payment in the queue.
        Assert.Equal("authorized", await db.GetStatusAsync(id, Ct));
        Assert.Equal(1L, await db.CountPendingAsync(Ct));
        Assert.Equal(0L, await db.CountOutboxAsync(Ct));
    }

    [Theory]
    [InlineData("authorized")]
    [InlineData("captured")]
    [InlineData("declined")]
    [InlineData("voided")]
    public async Task Only_A_Created_Payment_Becomes_Authorized(string status)
    {
        var id = await db.InsertPaymentAsync(Age, status, Ct);
        var pending = await db.CountPendingAsync(Ct);

        // false tells the saga that the payment has moved on without it: it reads the row again and goes from there.
        Assert.False(await store.MarkAuthorizedAsync(id, Ct));

        Assert.Equal(status, await db.GetStatusAsync(id, Ct));
        Assert.Equal(pending, await db.CountPendingAsync(Ct));
    }

    [Fact]
    public async Task Captured_Payment_Gets_Its_Fee_An_Event_For_The_Ledger_And_One_Finalized_Event()
    {
        var id = await db.InsertPaymentAsync(Age, "authorized", Ct);

        Assert.True(await store.MarkCapturedAsync(Captured(id), "42:3", Ct));

        Assert.Equal("captured", await db.GetStatusAsync(id, Ct));
        Assert.Equal(3_000L, await db.GetFeeAsync(id, Ct));
        Assert.Equal(0L, await db.CountPendingAsync(Ct));

        var (key, _) = Assert.Single(await db.OutboxAsync(Topics.PaymentsCaptured, Ct));
        Assert.Equal("42:3", key);
        await AssertFinalizedEventAsync(id, "42:3", PaymentFinalStatus.Captured);
    }

    [Theory]
    [InlineData("created")]         // the bank approved, but the antifraud has not been asked
    [InlineData("captured")]
    [InlineData("declined")]
    [InlineData("voided")]
    public async Task Only_An_Authorized_Payment_Is_Captured(string status)
    {
        var id = await db.InsertPaymentAsync(Age, status, Ct);

        Assert.False(await store.MarkCapturedAsync(Captured(id), "42", Ct));

        Assert.Equal(status, await db.GetStatusAsync(id, Ct));
        Assert.Equal(0L, await db.CountOutboxAsync(Ct));            // no event of either kind without the capture
    }

    [Fact]
    public async Task Declined_Payment_Leaves_The_Queue_With_A_Finalized_Event_Only()
    {
        var id = await db.InsertPaymentAsync(Age, "created", Ct);

        Assert.True(await store.MarkDeclinedAsync(id, "42", Ct));

        Assert.Equal("declined", await db.GetStatusAsync(id, Ct));
        Assert.Equal(0L, await db.CountPendingAsync(Ct));
        Assert.Equal(0L, await db.CountOutboxAsync(Topics.PaymentsCaptured, Ct));   // no money moved: nothing for the ledger
        await AssertFinalizedEventAsync(id, "42", PaymentFinalStatus.Declined);
    }

    [Theory]
    [InlineData("authorized")]      // the bank approved: a decline would lose the hold
    [InlineData("captured")]
    [InlineData("declined")]
    [InlineData("voided")]
    public async Task Only_A_Created_Payment_Is_Declined(string status)
    {
        var id = await db.InsertPaymentAsync(Age, status, Ct);

        Assert.False(await store.MarkDeclinedAsync(id, "42", Ct));

        Assert.Equal(status, await db.GetStatusAsync(id, Ct));
        Assert.Equal(0L, await db.CountOutboxAsync(Ct));            // no event without the transition
    }

    [Theory]
    [InlineData("created")]
    [InlineData("authorized")]
    public async Task Voided_Payment_Leaves_The_Queue_With_A_Finalized_Event_Only(string status)
    {
        var id = await db.InsertPaymentAsync(Age, status, Ct);

        Assert.True(await store.MarkVoidedAsync(id, "42", Ct));

        Assert.Equal("voided", await db.GetStatusAsync(id, Ct));
        Assert.Equal(0L, await db.CountPendingAsync(Ct));      // final: recovery has nothing left to do
        Assert.Equal(0L, await db.CountOutboxAsync(Topics.PaymentsCaptured, Ct));   // no money moved: nothing for the ledger
        Assert.Null(await db.GetFeeAsync(id, Ct));             // and no fee
        await AssertFinalizedEventAsync(id, "42", PaymentFinalStatus.Voided);
    }

    [Theory]
    [InlineData("captured")]
    [InlineData("declined")]
    public async Task Void_Of_A_Captured_Or_Declined_Payment_Changes_Nothing(string status)
    {
        var id = await db.InsertPaymentAsync(Age, status, Ct);

        Assert.False(await store.MarkVoidedAsync(id, "42", Ct));

        Assert.Equal(status, await db.GetStatusAsync(id, Ct));
        Assert.Equal(0L, await db.CountOutboxAsync(Ct));
    }

    [Fact]
    public async Task Second_Void_Of_A_Payment_Returns_False()
    {
        var id = await db.InsertPaymentAsync(Age, "authorized", Ct);
        Assert.True(await store.MarkVoidedAsync(id, "42", Ct));

        // A second writer of the same outcome, the endpoint and recovery for one payment: the second one learns that
        // the payment is already settled and records nothing, its event neither.
        Assert.False(await store.MarkVoidedAsync(id, "42", Ct));

        Assert.Equal("voided", await db.GetStatusAsync(id, Ct));
        Assert.Equal(0L, await db.CountPendingAsync(Ct));
        Assert.Equal(1L, await db.CountOutboxAsync(Topics.PaymentsFinalized, Ct));
    }

    private static PaymentCaptured Captured(Guid id) => new(id, 42, 150_000, 3_000, "RUB", DateTimeOffset.UtcNow);

    // The one PaymentFinalized of the payment, read back with the serializer its consumers use, says what the row says.
    private async Task AssertFinalizedEventAsync(Guid id, string key, string status)
    {
        var (eventKey, payload) = Assert.Single(await db.OutboxAsync(Topics.PaymentsFinalized, Ct));
        Assert.Equal(key, eventKey);

        var finalized = JsonSerializer.Deserialize(payload, ContractsJson.Default.PaymentFinalized);
        Assert.Equal(await db.FinalizedFromRowAsync(id, Ct), finalized);
        Assert.Equal(status, finalized!.Status);
    }
}
