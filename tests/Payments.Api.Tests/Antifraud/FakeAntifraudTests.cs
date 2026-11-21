using Microsoft.Extensions.Options;
using Payments.Api.Antifraud;
using Payments.Api.Payments;

namespace Payments.Api.Tests.Antifraud;

/// <summary>The antifraud of the stands without the service: the share it denies, and the same answer to a repeat.</summary>
public sealed class FakeAntifraudTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Fake_Antifraud_Allows_Every_Payment_By_Default()
    {
        var fake = Fake(denyRatePercent: 0);

        foreach (var payment in Payments(1000))
            Assert.Equal(AntifraudOutcome.Allow, (await fake.CheckAsync(payment, Ct)).Outcome);
    }

    [Fact]
    public async Task Fake_Antifraud_Denies_About_Its_Share_Of_Payments()
    {
        var fake = Fake(denyRatePercent: 30);

        var denied = 0;
        foreach (var payment in Payments(2000))
            if ((await fake.CheckAsync(payment, Ct)).Outcome == AntifraudOutcome.Deny)
                denied++;

        // 600 expected; the bounds are about seven standard deviations away, so the test does not flicker.
        Assert.InRange(denied, 450, 750);
    }

    [Fact]
    public async Task Fake_Antifraud_Answers_A_Repeated_Check_As_The_First()
    {
        // Recovery checks a payment again after a failure: the service answers with its stored decision, and so must the fake.
        var fake = Fake(denyRatePercent: 50);

        foreach (var payment in Payments(200))
            Assert.Equal((await fake.CheckAsync(payment, Ct)).Outcome, (await fake.CheckAsync(payment, Ct)).Outcome);
    }

    private static FakeAntifraud Fake(int denyRatePercent) =>
        new(Options.Create(new AntifraudOptions { DenyRatePercent = denyRatePercent }));

    // UUIDv7, as the endpoint creates them.
    private static IEnumerable<Payment> Payments(int count) =>
        Enumerable.Range(0, count).Select(_ => new Payment(
            Guid.CreateVersion7(), 42, "key", "hash", 150000, "RUB", Fee: null, PaymentStatus.Authorized, DateTimeOffset.UtcNow));
}
