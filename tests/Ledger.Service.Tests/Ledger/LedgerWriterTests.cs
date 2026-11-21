using Contracts;
using Ledger.Service.Ledger;
using Ledger.Service.Tests.Infrastructure;
using Npgsql;
using Payments.Api.Tests.Infrastructure;
using static Ledger.Service.Tests.Infrastructure.LedgerData;

namespace Ledger.Service.Tests.Ledger;

/// <summary>
/// What a batch books on a real Postgres: three postings per payment, on the sub-accounts of the batch's partition,
/// and nothing for a payment booked before, however it comes again.
/// </summary>
[Collection("Postgres")]
public sealed class LedgerWriterTests(PostgresFixture db) : IAsyncLifetime
{
    private readonly LedgerWriter writer = new(db.DataSource);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => await db.ResetAsync(Ct);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task Payment_Is_Booked_As_Three_Postings_That_Sum_To_Zero()
    {
        var payment = Payment(merchantId: 42, amount: 150_000, fee: 3_000);

        Assert.Equal(1, await writer.WriteBatchAsync(3, [payment], Ct));

        // The ids are spelled out rather than built by LedgerAccounts: they are data in the tables, and a new format
        // would leave the balances already booked under the old one.
        var postings = await db.PostingsOfAsync(payment.PaymentId, Ct);
        Assert.Equal(new[] { ("clearing:p3", -150_000L), ("fee:p3", 3_000L), ("merchant:42:p3", 147_000L) }, postings);
        Assert.Equal(0, postings.Sum(p => p.Amount));

        // Each balance is the sum of its postings, and the payment is marked as booked.
        Assert.Equal(
            Balances(("clearing:p3", -150_000), ("fee:p3", 3_000), ("merchant:42:p3", 147_000)),
            await db.AccountsAsync(Ct));
        Assert.Equal(1L, await db.CountProcessedPaymentsAsync(Ct));
    }

    [Fact]
    public async Task Payments_Of_One_Merchant_In_A_Batch_Add_Up_On_Its_Sub_Account()
    {
        // Three payments, three postings to each of the batch's accounts. The upsert sums them per account first: given
        // two rows for one account, INSERT ... ON CONFLICT DO UPDATE fails ("cannot affect row a second time").
        Assert.Equal(3, await writer.WriteBatchAsync(0,
            [Payment(42, 100_000, 2_000), Payment(42, 50_000, 1_000), Payment(42, 10_000, 200)], Ct));

        // The next batch adds to the balances it finds.
        Assert.Equal(1, await writer.WriteBatchAsync(0, [Payment(42, 40_000, 800)], Ct));

        Assert.Equal(
            Balances(("clearing:p0", -200_000), ("fee:p0", 4_000), ("merchant:42:p0", 196_000)),
            await db.AccountsAsync(Ct));
        Assert.Equal(12L, await db.CountPostingsAsync(Ct));
    }

    [Fact]
    public async Task Redelivered_Batch_Changes_Nothing()
    {
        // After a failure the consumer reads its uncommitted messages again: Kafka delivers at least once.
        PaymentCaptured[] batch = [Payment(42), Payment(43), Payment(44)];
        await writer.WriteBatchAsync(5, batch, Ct);
        var accounts = await db.AccountsAsync(Ct);

        Assert.Equal(0, await writer.WriteBatchAsync(5, batch, Ct));

        Assert.Equal(accounts, await db.AccountsAsync(Ct));
        Assert.Equal(9L, await db.CountPostingsAsync(Ct));
        Assert.Equal(3L, await db.CountProcessedPaymentsAsync(Ct));
    }

    [Fact]
    public async Task Batch_Of_Booked_And_New_Payments_Books_Only_The_New_Ones()
    {
        // A batch read again need not end where the first one did: the reader takes what has arrived since as well.
        var booked = Payment(42, 100_000, 2_000);
        await writer.WriteBatchAsync(1, [booked], Ct);

        var applied = await writer.WriteBatchAsync(1, [booked, Payment(42, 50_000, 1_000)], Ct);

        // The count is of what was booked: the consumer's latency metric leaves out a batch with repeats in it.
        Assert.Equal(1, applied);
        Assert.Equal(
            Balances(("clearing:p1", -150_000), ("fee:p1", 3_000), ("merchant:42:p1", 147_000)),
            await db.AccountsAsync(Ct));
    }

    [Fact]
    public async Task Payment_Twice_In_One_Batch_Is_Booked_Once()
    {
        // The relay sends its batch again if it fails after Kafka took it, and a consumer that lags reads both copies
        // in one batch.
        var payment = Payment(42, 150_000, 3_000);

        Assert.Equal(1, await writer.WriteBatchAsync(4, [payment, payment], Ct));

        // DISTINCT ON keeps one copy. Without it both copies join the one row that processed_payments lets through, and
        // the payment gets six postings. The invariants would not see it: the six sum to zero, the balances match them.
        Assert.Equal(3, (await db.PostingsOfAsync(payment.PaymentId, Ct)).Count);
        Assert.Equal(
            Balances(("clearing:p4", -150_000), ("fee:p4", 3_000), ("merchant:42:p4", 147_000)),
            await db.AccountsAsync(Ct));
    }

    [Fact]
    public async Task Each_Partition_Books_A_Large_Merchant_On_A_Sub_Account_Of_Its_Own()
    {
        // The payments of a large merchant are spread over the partitions (Topics.PaymentsCapturedKey), and the consumer
        // of each partition writes them to the partition's own accounts: no row has two writers, and no batch waits for
        // a lock another one holds (experiments 4 and 5).
        await writer.WriteBatchAsync(0, [Payment(1, 100_000, 2_000)], Ct);
        await writer.WriteBatchAsync(3, [Payment(1, 50_000, 1_000), Payment(1, 50_000, 1_000)], Ct);
        await writer.WriteBatchAsync(7, [Payment(1, 10_000, 200)], Ct);

        Assert.Equal(
            Balances(
                ("clearing:p0", -100_000), ("clearing:p3", -100_000), ("clearing:p7", -10_000),
                ("fee:p0", 2_000), ("fee:p3", 2_000), ("fee:p7", 200),
                ("merchant:1:p0", 98_000), ("merchant:1:p3", 98_000), ("merchant:1:p7", 9_800)),
            await db.AccountsAsync(Ct));
    }

    [Fact]
    public async Task Two_Writers_Of_One_Batch_At_Once_Book_It_Once()
    {
        // A consumer that stalled past its session timeout has lost its partition. Waking up, it finishes the batch it was
        // writing, while the new owner writes the same messages, read again from the last committed offset.
        var batch = Enumerable.Range(0, 50).Select(_ => Payment(42)).ToArray();

        // A gate for both writes: INSERT waits while another transaction holds a SHARE lock on the table.
        await using var gate = await db.DataSource.OpenConnectionAsync(Ct);
        await using var gateTransaction = await gate.BeginTransactionAsync(Ct);
        await using (var share = new NpgsqlCommand("LOCK TABLE processed_payments IN SHARE MODE", gate, gateTransaction))
            await share.ExecuteNonQueryAsync(Ct);

        var writes = new[] { writer.WriteBatchAsync(5, batch, Ct), writer.WriteBatchAsync(5, batch, Ct) };
        await Eventually.WaitUntilAsync(async () => await CountWritesAtTheGateAsync() == 2);
        await gateTransaction.RollbackAsync(Ct);

        // Let go together, they race for the same rows of processed_payments. The second to reach a payment waits for the
        // first one's transaction to end, then skips the payment: in READ COMMITTED, ON CONFLICT DO NOTHING sees a row
        // committed after its own statement began.
        var applied = await Task.WhenAll(writes);

        Assert.Equal(batch.Length, applied.Sum());
        Assert.Equal(3L * batch.Length, await db.CountPostingsAsync(Ct));
        Assert.Equal(
            Balances(("clearing:p5", -50 * 150_000), ("fee:p5", 50 * 3_000), ("merchant:42:p5", 50 * 147_000)),
            await db.AccountsAsync(Ct));
    }

    private async Task<long> CountWritesAtTheGateAsync()
    {
        await using var command = db.DataSource.CreateCommand(
            "SELECT count(*) FROM pg_locks WHERE relation = 'processed_payments'::regclass AND NOT granted");
        return (long)(await command.ExecuteScalarAsync(Ct))!;
    }
}
