using System.Globalization;

namespace Contracts;

public static class Topics
{
    /// <summary>
    /// Captured payments. The message key is the merchant id, so all payments of one merchant
    /// land in the same partition and are applied to the ledger by the same consumer; those of a large merchant
    /// are spread over the partitions (<see cref="PaymentsCapturedKey"/>).
    /// </summary>
    public const string PaymentsCaptured = "payments.captured";

    /// <summary>
    /// The maximum number of Ledger consumers working in parallel. Every partition has exactly one writer
    /// and its own sub-accounts of merchants, clearing and fees in the ledger.
    /// </summary>
    public const int PaymentsCapturedPartitions = 8;

    /// <summary>
    /// The message key of a captured payment: the merchant id, or for a large merchant, whose payments one partition and
    /// its one writer cannot keep up with, the merchant id with a salt from the payment id, merchantId:0 to merchantId:7.
    /// The order of a merchant's payments across partitions is lost; the ledger does not need it: balances are sums,
    /// and processed_payments skips a repeat whatever partition it comes from.
    /// </summary>
    /// <remarks>
    /// The salt is the last byte of the payment id, random in a UUIDv7 as in a v4, modulo the number of partitions.
    /// The producer's default partitioner (librdkafka's consistent_random) takes the CRC32 of the key modulo the number
    /// of partitions. The eight keys of a merchant differ only in the low three bits of their last byte, and CRC32 is
    /// linear, so with 8 partitions they land in 8 different ones, whatever the merchant id. The end-to-end test of the
    /// hot merchant checks it with a real Kafka.
    /// </remarks>
    public static string PaymentsCapturedKey(long merchantId, Guid paymentId, bool spread)
    {
        if (!spread)
            return merchantId.ToString(CultureInfo.InvariantCulture);

        Span<byte> bytes = stackalloc byte[16];
        paymentId.TryWriteBytes(bytes);
        var salt = bytes[15] % PaymentsCapturedPartitions;

        return string.Create(CultureInfo.InvariantCulture, $"{merchantId}:{salt}");
    }
}
