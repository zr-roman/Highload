using Contracts;
using Ledger.Service.Ledger;
using Ledger.Service.Tests.Infrastructure;
using Npgsql;
using static Ledger.Service.Tests.Infrastructure.LedgerData;

namespace Ledger.Service.Tests.Ledger;

/// <summary>
/// What the API of the ledger answers, on books the writer made: a merchant's balance, the platform's fees, the
/// invariants. And for each of the three checks of the invariants, a fault that only this check catches.
/// </summary>
[Collection("Postgres")]
public sealed class LedgerQueriesTests(PostgresFixture db) : IAsyncLifetime
{
    private readonly LedgerWriter writer = new(db.DataSource);
    private readonly LedgerQueries queries = new(db.DataSource);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => await db.ResetAsync(Ct);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task Merchant_Balance_Is_The_Sum_Of_Its_Sub_Accounts()
    {
        await writer.WriteBatchAsync(0, [Payment(1, 100_000, 2_000), Payment(11, 70_000, 1_400)], Ct);
        await writer.WriteBatchAsync(3, [Payment(1, 50_000, 1_000)], Ct);
        await writer.WriteBatchAsync(7, [Payment(1, 10_000, 200), Payment(11, 30_000, 600)], Ct);

        // The same accounts as GET /balances/merchants/{id} asks for. Those of merchant 11 begin with "merchant:1" too:
        // the ids are compared whole.
        Assert.Equal(98_000L + 49_000 + 9_800, await queries.GetBalanceAsync(LedgerAccounts.MerchantAccounts(1), Ct));
        Assert.Equal(68_600L + 29_400, await queries.GetBalanceAsync(LedgerAccounts.MerchantAccounts(11), Ct));
    }

    [Fact]
    public async Task Balance_Booked_Before_The_Sub_Accounts_Still_Counts()
    {
        // Before the sub-accounts a merchant had one account, merchant:{id}. The ledger is append-only: what was booked
        // to it stays there.
        await BookAsBeforeTheSubAccountsAsync(Payment(42, 100_000, 2_000), merchantAccount: "merchant:42", partition: 2);
        Assert.Equal(98_000L, await queries.GetBalanceAsync(LedgerAccounts.MerchantAccounts(42), Ct));

        await writer.WriteBatchAsync(2, [Payment(42, 50_000, 1_000)], Ct);
        Assert.Equal(98_000L + 49_000, await queries.GetBalanceAsync(LedgerAccounts.MerchantAccounts(42), Ct));
    }

    [Fact]
    public async Task Merchant_With_Nothing_Booked_Has_No_Balance()
    {
        await writer.WriteBatchAsync(0, [Payment(42)], Ct);

        // null rather than 0: the endpoint answers 404, the ledger does not know this merchant.
        Assert.Null(await queries.GetBalanceAsync(LedgerAccounts.MerchantAccounts(43), Ct));
    }

    [Fact]
    public async Task Platform_Fees_Are_The_Sum_Of_The_Fee_Sub_Accounts()
    {
        Assert.Equal(0L, await queries.GetPlatformFeesAsync(Ct));      // 0 rather than null on an empty ledger

        await writer.WriteBatchAsync(0, [Payment(1, 100_000, 2_000), Payment(2, 50_000, 1_000)], Ct);
        await writer.WriteBatchAsync(5, [Payment(3, 10_000, 200)], Ct);

        Assert.Equal(3_200L, await queries.GetPlatformFeesAsync(Ct));
    }

    [Fact]
    public async Task Books_The_Writer_Makes_Hold_The_Invariants()
    {
        Assert.Equal(new LedgerInvariants(0, 0, 0, 0), await queries.CheckInvariantsAsync(Ct));

        var repeated = Payment(1);
        await writer.WriteBatchAsync(0, [Payment(1), repeated, Payment(2)], Ct);
        await writer.WriteBatchAsync(4, [Payment(1), Payment(3), Payment(3, 70_000, 1_400)], Ct);
        await writer.WriteBatchAsync(0, [repeated, Payment(2)], Ct);       // a redelivery, with a new payment after it

        var invariants = await queries.CheckInvariantsAsync(Ct);
        Assert.Equal(new LedgerInvariants(UnbalancedPayments: 0, MismatchedAccounts: 0, TotalBalance: 0, Payments: 7), invariants);
        Assert.True(invariants.Ok);
    }

    [Fact]
    public async Task Posting_Filed_Under_Another_Payment_Is_Caught_By_The_Payment_Check_Alone()
    {
        var (first, second) = await BookTwoPaymentsAsync();

        // The fee posting of the first payment is filed under the second one. Every balance still matches its postings,
        // and the balances still sum to zero; only the sum of each payment's postings is off, for both payments.
        await db.ExecuteAsync(
            $"UPDATE postings SET payment_id = '{second.PaymentId}' WHERE payment_id = '{first.PaymentId}' AND account_id = 'fee:p0'",
            Ct);

        var invariants = await queries.CheckInvariantsAsync(Ct);
        Assert.Equal(new LedgerInvariants(UnbalancedPayments: 2, MismatchedAccounts: 0, TotalBalance: 0, Payments: 2), invariants);
        Assert.False(invariants.Ok);
    }

    [Fact]
    public async Task Money_Moved_Between_Balances_Without_Postings_Is_Caught_By_The_Account_Check_Alone()
    {
        await BookTwoPaymentsAsync();

        // An extra fee taken from the merchant by an UPDATE of the balances, with no postings for it. The money only
        // moved, so the total is still zero, and the postings of the payments are untouched.
        await db.ExecuteAsync("""
            UPDATE accounts SET balance = balance + CASE id WHEN 'merchant:42:p0' THEN -1000 ELSE 1000 END
            WHERE id IN ('merchant:42:p0', 'fee:p0')
            """, Ct);

        var invariants = await queries.CheckInvariantsAsync(Ct);
        Assert.Equal(new LedgerInvariants(UnbalancedPayments: 0, MismatchedAccounts: 2, TotalBalance: 0, Payments: 2), invariants);
        Assert.False(invariants.Ok);
    }

    [Fact]
    public async Task Postings_Of_An_Account_With_No_Row_Are_Caught_By_The_Total_Alone()
    {
        await BookTwoPaymentsAsync();

        // postings has no foreign key to accounts, on purpose (schema.sql), so a posting may name an account that has no
        // row. The account check starts from the rows and does not see such postings, and the payments are balanced.
        // Only the total shows the fees that are in the journal but on no balance.
        await db.ExecuteAsync("DELETE FROM accounts WHERE id = 'fee:p0'", Ct);

        var invariants = await queries.CheckInvariantsAsync(Ct);
        Assert.Equal(new LedgerInvariants(UnbalancedPayments: 0, MismatchedAccounts: 0, TotalBalance: -3_000, Payments: 2), invariants);
        Assert.False(invariants.Ok);
    }

    // Correct books of two payments, for the tests above to break.
    private async Task<(PaymentCaptured First, PaymentCaptured Second)> BookTwoPaymentsAsync()
    {
        var first = Payment(42, 100_000, 2_000);
        var second = Payment(42, 50_000, 1_000);
        await writer.WriteBatchAsync(0, [first, second], Ct);

        Assert.True((await queries.CheckInvariantsAsync(Ct)).Ok);
        return (first, second);
    }

    // One payment booked the way the writer did it before the sub-accounts: the merchant's posting to merchant:{id}.
    private async Task BookAsBeforeTheSubAccountsAsync(PaymentCaptured payment, string merchantAccount, int partition)
    {
        await using var command = db.DataSource.CreateCommand("""
            WITH processed AS (
                INSERT INTO processed_payments (payment_id) VALUES ($1)
            ), booked AS (
                INSERT INTO postings (payment_id, account_id, amount) VALUES ($1, $2, -$5), ($1, $3, $5 - $6), ($1, $4, $6)
            )
            INSERT INTO accounts (id, balance) VALUES ($2, -$5), ($3, $5 - $6), ($4, $6)
            """);
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = payment.PaymentId });
        command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = LedgerAccounts.Clearing(partition) });
        command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = merchantAccount });
        command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = LedgerAccounts.Fee(partition) });
        command.Parameters.Add(new NpgsqlParameter<long> { TypedValue = payment.Amount });
        command.Parameters.Add(new NpgsqlParameter<long> { TypedValue = payment.Fee });
        await command.ExecuteNonQueryAsync(Ct);
    }
}
