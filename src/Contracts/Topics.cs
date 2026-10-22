namespace Contracts;

public static class Topics
{
    /// <summary>
    /// Captured payments. The message key is the merchant id, so all payments of one merchant
    /// land in the same partition and are applied to the ledger by the same consumer.
    /// </summary>
    public const string PaymentsCaptured = "payments.captured";

    /// <summary>
    /// The maximum number of Ledger consumers working in parallel. Every partition has exactly one writer
    /// and its own clearing and fee sub-accounts in the ledger.
    /// </summary>
    public const int PaymentsCapturedPartitions = 8;
}
