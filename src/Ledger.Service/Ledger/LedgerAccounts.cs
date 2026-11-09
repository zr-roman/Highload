using Contracts;
using System.Globalization;

namespace Ledger.Service.Ledger;

/// <summary>Account ids. The same format is built in SQL in <see cref="LedgerWriter"/>.</summary>
public static class LedgerAccounts
{
    /// <summary>
    /// What the ledger owes a merchant, one sub-account per Kafka partition its payments came from: one for most merchants,
    /// several for a large merchant, whose payments are spread over the partitions (Topics.PaymentsCapturedKey).
    /// Each sub-account has one writer, the consumer of its partition. The merchant's balance is their sum.
    /// </summary>
    public static string Merchant(long merchantId, int partition) =>
        string.Create(CultureInfo.InvariantCulture, $"merchant:{merchantId}:p{partition}");

    /// <summary>
    /// Every account that may hold a merchant's money: its sub-accounts, and merchant:{id}, the one account a merchant had
    /// before them. The ledger is append-only, and the postings already booked to it stay there.
    /// </summary>
    public static string[] MerchantAccounts(long merchantId) =>
    [
        string.Create(CultureInfo.InvariantCulture, $"merchant:{merchantId}"),
        .. Enumerable.Range(0, Topics.PaymentsCapturedPartitions).Select(partition => Merchant(merchantId, partition)),
    ];

    /// <summary>Money received from the acquirer, one sub-account per Kafka partition.</summary>
    public static string Clearing(int partition) =>
        string.Create(CultureInfo.InvariantCulture, $"clearing:p{partition}");

    /// <summary>Platform fee income, one sub-account per Kafka partition. The total is their sum.</summary>
    public static string Fee(int partition) =>
        string.Create(CultureInfo.InvariantCulture, $"fee:p{partition}");
}
