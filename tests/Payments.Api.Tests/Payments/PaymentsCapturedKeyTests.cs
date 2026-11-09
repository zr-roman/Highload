using Contracts;

namespace Payments.Api.Tests.Payments;

/// <summary>
/// The key of a captured payment, which decides its partition of payments.captured and so its writer in the ledger.
/// That the eight keys of a large merchant land in eight partitions, the end-to-end test of the hot merchant checks
/// with a real Kafka.
/// </summary>
public sealed class PaymentsCapturedKeyTests
{
    [Fact]
    public void A_Merchant_Without_A_Spread_Keeps_Its_Id_As_The_Key()
    {
        var keys = Enumerable.Range(0, 100).Select(_ => Topics.PaymentsCapturedKey(42, Guid.CreateVersion7(), spread: false));

        Assert.All(keys, key => Assert.Equal("42", key));
    }

    [Fact]
    public void A_Large_Merchant_Spreads_Its_Payments_Over_As_Many_Keys_As_There_Are_Partitions()
    {
        var keys = Enumerable.Range(0, 1000).Select(_ => Topics.PaymentsCapturedKey(42, Guid.CreateVersion7(), spread: true)).ToHashSet();

        var expected = Enumerable.Range(0, Topics.PaymentsCapturedPartitions).Select(salt => $"42:{salt}");
        Assert.Equal(expected.Order(), keys.Order());
    }
}
