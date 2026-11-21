using System.Buffers.Binary;
using Microsoft.Extensions.Options;
using Payments.Api.Payments;

namespace Payments.Api.Antifraud;

/// <summary>
/// Decides in the process: for the load matrix and the tests that run the platform without the antifraud service
/// (Antifraud:Mode = Fake). Denies Antifraud:DenyRatePercent of the payments and allows the rest, so a stand without
/// the service still goes through the compensation of the saga, the void.
/// </summary>
/// <remarks>
/// The decision follows from the payment id, as the service keeps its decision by payment id: a repeated check, by
/// recovery after a failure, gets the same answer. The last bytes of a UUID are random, both in v4 and in v7, the
/// UUIDs of the endpoint: the share of denied payments is the same in any order of payments.
/// </remarks>
public sealed class FakeAntifraud(IOptions<AntifraudOptions> options) : IAntifraud
{
    private static readonly AntifraudDecision Allow = new(AntifraudOutcome.Allow, Score: 0, Reasons: [], ModelVersion: "fake");
    private static readonly AntifraudDecision Deny = new(AntifraudOutcome.Deny, Score: 1, Reasons: ["fake"], ModelVersion: "fake");

    public Task<AntifraudDecision> CheckAsync(Payment payment, CancellationToken cancellationToken)
    {
        var bucket = BinaryPrimitives.ReadUInt32LittleEndian(payment.Id.ToByteArray().AsSpan(12)) % 100;
        return Task.FromResult(bucket < options.Value.DenyRatePercent ? Deny : Allow);
    }
}
