using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Payments.Api.Acquiring;
using Payments.Api.Tests.Infrastructure;

namespace Payments.Api.Tests.Recovery;

/// <summary>When recovery takes the next batch, which instance runs it, and how it stops.</summary>
[Collection("Postgres")]
public sealed class PaymentRecoveryLoopTests(PostgresFixture db) : IAsyncLifetime
{
    private static readonly TimeSpan Stale = TimeSpan.FromMinutes(1);

    // Real time in which another pass would surely have started if nothing held it back.
    // The fake clock does not move meanwhile, so a pause of Interval cannot end on its own.
    private static readonly TimeSpan QuietPeriod = TimeSpan.FromMilliseconds(300);

    private readonly FakeBank bank = new();
    private readonly FakeTimeProvider time = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => await db.ResetAsync(Ct);

    public ValueTask DisposeAsync()
    {
        Assert.Empty(bank.Unscripted);
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task Full_Batch_Is_Followed_By_The_Next_One_At_Once()
    {
        var ids = new List<Guid>();
        for (var i = 0; i < 5; i++)
        {
            var id = await db.InsertPaymentAsync(Stale, cancellationToken: Ct);
            bank.Answer(id, AcquirerOutcome.Approved);
            ids.Add(id);
        }

        await using var host = await RecoveryHost.StartAsync(db, bank, time, batchSize: 2, cancellationToken: Ct);

        // 2 + 2 + 1, and the clock never moves: only the last, partial batch ends with a pause.
        await host.WaitForPassesAsync(3);
        foreach (var id in ids)
            Assert.Equal("captured", await db.GetStatusAsync(id, Ct));
    }

    [Fact]
    public async Task After_A_Partial_Batch_Recovery_Waits_Exactly_Interval()
    {
        var first = await db.InsertPaymentAsync(Stale, cancellationToken: Ct);
        bank.Answer(first, AcquirerOutcome.Approved);

        await using var host = await RecoveryHost.StartAsync(db, bank, time, cancellationToken: Ct);
        await host.WaitForPassesAsync(1);

        // Stuck after the pass: recovery sees it only when the pause is over.
        var second = await db.InsertPaymentAsync(Stale, cancellationToken: Ct);
        bank.Answer(second, AcquirerOutcome.Approved);
        await Task.Delay(QuietPeriod, Ct);
        Assert.Equal(0, bank.LookupsFor(second));

        time.Advance(RecoveryHost.Interval - TimeSpan.FromSeconds(1));
        await Task.Delay(QuietPeriod, Ct);
        Assert.Equal(0, bank.LookupsFor(second));                 // a second short of the pause: still waiting

        time.Advance(TimeSpan.FromSeconds(1));
        await Eventually.WaitUntilAsync(async () => await db.GetStatusAsync(second, Ct) == "captured");
    }

    [Fact]
    public async Task Full_Batch_That_Always_Fails_Does_Not_Spin()
    {
        var a = await db.InsertPaymentAsync(Stale, cancellationToken: Ct);
        var b = await db.InsertPaymentAsync(Stale, cancellationToken: Ct);
        bank.Fail(a, b);

        await using var host = await RecoveryHost.StartAsync(db, bank, time, batchSize: 2, cancellationToken: Ct);
        await host.WaitForPassesAsync(1);

        // A full batch normally means "take the next one at once", but this one moved nothing.
        await Task.Delay(QuietPeriod, Ct);
        Assert.Equal(2, bank.Lookups);

        time.Advance(RecoveryHost.Interval);
        await host.WaitForPassesAsync(2);
        await Task.Delay(QuietPeriod, Ct);
        Assert.Equal(4, bank.Lookups);                             // exactly one more pass per Interval
    }

    [Fact]
    public async Task Only_The_Leader_Asks_The_Bank()
    {
        var id = await db.InsertPaymentAsync(Stale, cancellationToken: Ct);
        var otherBank = new FakeBank();
        bank.Answer(id, AcquirerOutcome.Approved);
        otherBank.Answer(id, AcquirerOutcome.Approved);

        await using var first = await RecoveryHost.StartAsync(db, bank, time, cancellationToken: Ct);
        await using var second = await RecoveryHost.StartAsync(db, otherBank, new FakeTimeProvider(), cancellationToken: Ct);

        await Eventually.WaitUntilAsync(async () => await db.GetStatusAsync(id, Ct) == "captured");
        await Task.Delay(QuietPeriod, Ct);

        // One instance holds the advisory lock; the other waits for it and never polls.
        Assert.Equal(1, bank.Lookups + otherBank.Lookups);
        Assert.Single(new[] { first, second }, host => HasLog(host, "is the leader"));
    }

    [Fact]
    public async Task Another_Instance_Takes_Over_When_The_Leader_Stops()
    {
        var otherBank = new FakeBank();

        await using var leader = await RecoveryHost.StartAsync(db, bank, time, cancellationToken: Ct);
        await Eventually.WaitUntilAsync(() => HasLog(leader, "is the leader"));

        await using var follower = await RecoveryHost.StartAsync(db, otherBank, new FakeTimeProvider(), cancellationToken: Ct);
        await Task.Delay(QuietPeriod, Ct);
        Assert.False(HasLog(follower, "is the leader"));

        // The leader goes away. Its connection closes, and the lock with it, as when the process dies.
        await leader.DisposeAsync();

        var id = await db.InsertPaymentAsync(Stale, cancellationToken: Ct);
        otherBank.Answer(id, AcquirerOutcome.Approved);

        // The follower tries the lock again every 5 seconds.
        await Eventually.WaitUntilAsync(async () => await db.GetStatusAsync(id, Ct) == "captured", TimeSpan.FromSeconds(15));
        Assert.True(HasLog(follower, "is the leader"));
    }

    [Fact]
    public async Task Stop_During_A_Pass_Leaves_The_Payment_For_The_Next_Run()
    {
        var id = await db.InsertPaymentAsync(Stale, cancellationToken: Ct);
        var asked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bank.Script(id, async cancellationToken =>
        {
            asked.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);    // the bank hangs until recovery stops
            return null;
        });

        await using var stopped = await RecoveryHost.StartAsync(db, bank, time, cancellationToken: Ct);
        await asked.Task.WaitAsync(Eventually.DefaultTimeout, Ct);

        await stopped.DisposeAsync();                                   // would throw if the loop crashed instead of stopping

        Assert.Equal("created", await db.GetStatusAsync(id, Ct));
        Assert.DoesNotContain(stopped.Logs.GetSnapshot(), r => r.Level >= LogLevel.Error);

        // The next run, after a restart, picks the payment up.
        var nextBank = new FakeBank();
        nextBank.Answer(id, AcquirerOutcome.Approved);
        await using var next = await RecoveryHost.StartAsync(db, nextBank, new FakeTimeProvider(), cancellationToken: Ct);
        await Eventually.WaitUntilAsync(async () => await db.GetStatusAsync(id, Ct) == "captured");
    }

    private static bool HasLog(RecoveryHost host, string text) =>
        host.Logs.GetSnapshot().Any(r => r.Message.Contains(text, StringComparison.Ordinal));
}
