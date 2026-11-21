using Payments.Api.Payments;
using Payments.Api.Tests.Infrastructure;

namespace Payments.Api.Tests.Payments;

/// <summary>
/// The transitions of the saga in PaymentStore, on a real Postgres: each one moves a payment only out of its own
/// source statuses, and the queue of recovery and the outbox change in the same statement.
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

    [Theory]
    [InlineData("created")]
    [InlineData("authorized")]
    public async Task Voided_Payment_Leaves_The_Queue_Without_An_Outbox_Row(string status)
    {
        var id = await db.InsertPaymentAsync(Age, status, Ct);

        Assert.True(await store.MarkVoidedAsync(id, Ct));

        Assert.Equal("voided", await db.GetStatusAsync(id, Ct));
        Assert.Equal(0L, await db.CountPendingAsync(Ct));      // final: recovery has nothing left to do
        Assert.Equal(0L, await db.CountOutboxAsync(Ct));       // no money moved: nothing for the ledger
        Assert.Null(await db.GetFeeAsync(id, Ct));             // and no fee
    }

    [Theory]
    [InlineData("captured")]
    [InlineData("declined")]
    public async Task Void_Of_A_Captured_Or_Declined_Payment_Changes_Nothing(string status)
    {
        var id = await db.InsertPaymentAsync(Age, status, Ct);

        Assert.False(await store.MarkVoidedAsync(id, Ct));

        Assert.Equal(status, await db.GetStatusAsync(id, Ct));
    }

    [Fact]
    public async Task Second_Void_Of_A_Payment_Returns_False()
    {
        var id = await db.InsertPaymentAsync(Age, "authorized", Ct);
        Assert.True(await store.MarkVoidedAsync(id, Ct));

        // A second writer of the same outcome, the endpoint and recovery for one payment: the second one learns that
        // the payment is already settled and records nothing.
        Assert.False(await store.MarkVoidedAsync(id, Ct));

        Assert.Equal("voided", await db.GetStatusAsync(id, Ct));
        Assert.Equal(0L, await db.CountPendingAsync(Ct));
    }
}
