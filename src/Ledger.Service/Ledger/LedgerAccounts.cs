using System.Globalization;

namespace Ledger.Service.Ledger;

/// <summary>Account ids. The same format is built in SQL in <see cref="LedgerWriter"/>.</summary>
public static class LedgerAccounts
{
    public static string Merchant(long merchantId) =>
        string.Create(CultureInfo.InvariantCulture, $"merchant:{merchantId}");

    /// <summary>Money received from the acquirer, one sub-account per Kafka partition.</summary>
    public static string Clearing(int partition) =>
        string.Create(CultureInfo.InvariantCulture, $"clearing:p{partition}");

    /// <summary>Platform fee income, one sub-account per Kafka partition. The total is their sum.</summary>
    public static string Fee(int partition) =>
        string.Create(CultureInfo.InvariantCulture, $"fee:p{partition}");
}
