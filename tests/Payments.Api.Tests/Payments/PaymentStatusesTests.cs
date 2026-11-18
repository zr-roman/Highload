using Npgsql;
using Payments.Api.Payments;
using Payments.Api.Tests.Infrastructure;

namespace Payments.Api.Tests.Payments;

/// <summary>
/// The statuses of the saga, 'authorized' and 'voided', as the code reads them before the saga writes them
/// (stage 4, task 3): the queue of recovery, its search and GET /payments/{id}.
/// </summary>
[Collection("Postgres")]
public sealed class PaymentStatusesTests(PostgresFixture db) : IAsyncLifetime
{
    // Older than the staleAfter of the searches below.
    private static readonly TimeSpan Stale = TimeSpan.FromMinutes(1);

    private readonly PaymentStore store = new(db.DataSource);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => await db.ResetAsync(Ct);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Theory]
    [InlineData("created")]
    [InlineData("authorized")]
    [InlineData("captured")]
    [InlineData("declined")]
    [InlineData("voided")]
    public async Task Payment_Is_Read_In_Every_Status_Of_The_Schema(string status)
    {
        var id = await db.InsertPaymentAsync(Stale, status, Ct);

        // What GET /payments/{id} does: the store reads the row, the response writes the status back.
        var payment = await store.FindByIdAsync(id, Ct);

        Assert.NotNull(payment);
        Assert.Equal(status, PaymentResponse.From(payment).Status);
    }

    [Fact]
    public async Task Authorized_Payment_Stays_In_The_Queue_And_Is_Found_By_The_Search()
    {
        var id = await db.InsertPaymentAsync(Stale, cancellationToken: Ct);

        await db.SetStatusAsync(id, "authorized", Ct);

        Assert.Equal(1L, await db.CountPendingAsync(Ct));
        var found = Assert.Single(await store.FindStaleCreatedAsync(TimeSpan.FromSeconds(30), 100, Ct));
        Assert.Equal(PaymentStatus.Authorized, found.Status);
    }

    [Fact]
    public async Task Voided_Payment_Leaves_The_Queue()
    {
        var id = await db.InsertPaymentAsync(Stale, "authorized", Ct);

        await db.SetStatusAsync(id, "voided", Ct);

        Assert.Equal(0L, await db.CountPendingAsync(Ct));
        Assert.Empty(await store.FindStaleCreatedAsync(TimeSpan.FromSeconds(30), 100, Ct));
    }

    [Theory]
    [InlineData("captured")]
    [InlineData("declined")]
    [InlineData("voided")]
    public async Task Final_Payment_Left_In_The_Queue_Takes_No_Place_In_The_Batch(string status)
    {
        var final = await db.InsertPaymentAsync(TimeSpan.FromMinutes(2), status, Ct);
        await QueueAsync(final);
        var created = await db.InsertPaymentAsync(Stale, cancellationToken: Ct);

        var batch = await store.FindStaleCreatedAsync(TimeSpan.FromSeconds(30), 1, Ct);

        Assert.Equal(created, Assert.Single(batch).Id);
    }

    // A row of the queue that the statement giving the final status did not delete.
    private async Task QueueAsync(Guid id)
    {
        await using var command = db.DataSource.CreateCommand(
            "INSERT INTO pending_payments (id, created_at) SELECT id, created_at FROM payments WHERE id = $1");
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = id });
        await command.ExecuteNonQueryAsync(Ct);
    }
}
