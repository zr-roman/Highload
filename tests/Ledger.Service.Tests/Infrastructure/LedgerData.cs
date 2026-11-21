using Contracts;

namespace Ledger.Service.Tests.Infrastructure;

/// <summary>What the tests give the ledger and what they expect of it.</summary>
public static class LedgerData
{
    /// <summary>
    /// A captured payment as Payments.Api publishes it: a UUIDv7 id, amounts in kopecks. By default 1 500 roubles
    /// with the 2 % fee.
    /// </summary>
    public static PaymentCaptured Payment(long merchantId, long amount = 150_000, long fee = 3_000) =>
        new(Guid.CreateVersion7(), merchantId, amount, fee, "RUB", DateTimeOffset.UtcNow);

    /// <summary>Expected balances, in the form <see cref="PostgresFixture.AccountsAsync"/> reads them.</summary>
    public static SortedDictionary<string, long> Balances(params (string Account, long Balance)[] accounts)
    {
        var balances = new SortedDictionary<string, long>(StringComparer.Ordinal);
        foreach (var (account, balance) in accounts)
            balances.Add(account, balance);

        return balances;
    }
}
