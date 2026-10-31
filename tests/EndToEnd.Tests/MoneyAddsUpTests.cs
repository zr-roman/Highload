using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using EndToEnd.Tests.Infrastructure;
using Ledger.Service.Ledger;
using Npgsql;
using Payments.Api.Payments;
using Payments.Api.Tests.Infrastructure;

namespace EndToEnd.Tests;

/// <summary>
/// The README's guarantees, checked through the whole platform under concurrent load: no payment is charged twice,
/// no captured payment is lost or booked twice on its way through the outbox and Kafka, and the money in
/// Payments.Api and in Ledger.Service adds up. Also when the ledger stops in the middle of the load,
/// and when it reads again the payments it has already booked.
/// </summary>
/// <remarks>
/// The tests share one platform and leave their payments in it, so the checks cover everything in the databases
/// and hold whichever tests ran before.
/// </remarks>
public sealed class MoneyAddsUpTests(PlatformFixture platform, ITestOutputHelper output) : IClassFixture<PlatformFixture>
{
    private const int Merchants = 50;
    private const int DuplicatedOrders = 20;
    private const int CopiesOfDuplicate = 5;
    private const int Concurrency = 16;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Money_Adds_Up_Across_Services_Under_Concurrent_Load()
    {
        var orders = NewOrders(new Random(2026), singles: 2000);   // the same merchants and amounts on every run

        var answers = await PayAllAsync(orders);

        await AssertNoDoubleChargeAsync(orders, answers);
        var captured = await AssertMoneyAddsUpAsync();

        output.WriteLine($"{answers.Count} requests, {orders.Count} payments, {captured} captured and booked by the ledger");
    }

    [Fact]
    public async Task Money_Adds_Up_When_The_Ledger_Restarts_Under_Load()
    {
        var random = new Random(2027);
        var orders = NewOrders(random, singles: 2000);
        var ordersWhileDown = NewOrders(random, singles: 300);
        var bookedBefore = await BookedAsync();

        // 1. The load runs in the background, and the ledger books a part of it. The wait also ends when the load
        //    is over: if it failed, awaiting it below shows why.
        var load = PayAllAsync(orders);
        await Eventually.WaitUntilAsync(
            async () => load.IsCompleted || await BookedAsync() >= bookedBefore + 300,
            timeout: TimeSpan.FromSeconds(30));

        // 2. The ledger stops in the middle of the load: the batches it has read but not written are dropped.
        await platform.StopLedgerAsync();
        IReadOnlyCollection<Answer> answersWhileDown;
        try
        {
            // 3. Payments.Api goes on accepting payments, and their events wait in Kafka. The load may be over by now:
            //    a stop takes as long as it takes. So the test sends more payments itself, and these surely arrive
            //    while the ledger is down.
            answersWhileDown = await PayAllAsync(ordersWhileDown);
        }
        finally
        {
            // 4. A new instance joins the group and goes on from the committed offsets,
            //    so it reads again the batches the old one had read but not written.
            platform.StartLedger();
        }

        // 5. The same checks as without the restart, over both loads.
        var answers = await load;
        await AssertNoDoubleChargeAsync([.. orders, .. ordersWhileDown], [.. answers, .. answersWhileDown]);
        var captured = await AssertMoneyAddsUpAsync();

        output.WriteLine($"{captured} captured and booked by the ledger, {ordersWhileDown.Count} orders sent while it was down");
    }

    [Fact]
    public async Task Ledger_Books_Nothing_Twice_When_Its_Offset_Commits_Are_Lost()
    {
        // 1. Payments the ledger has booked. The load is not the point here, so there are fewer of them.
        var orders = NewOrders(new Random(2028), singles: 500);
        await AssertNoDoubleChargeAsync(orders, await PayAllAsync(orders));
        var captured = await AssertMoneyAddsUpAsync();
        var postings = await PostingsAsync();
        var balances = await AccountsAsync();

        // 2. The failure the ledger is built for: a batch is in its database, but the batch's offsets never reached Kafka,
        //    so after a restart the batch is read again. A test cannot crash the ledger at that very point, so it stops
        //    the ledger and takes its commits back. All of them: the worst case, every payment in the topic is read again.
        await platform.StopLedgerAsync();
        long reread;
        try
        {
            await platform.RewindLedgerAsync();
            reread = await platform.LedgerLagAsync();
        }
        finally
        {
            platform.StartLedger();
        }

        // 3. The ledger reads the whole topic again, and processed_payments skips every payment in it:
        //    not one posting is added, not one balance changes.
        Assert.True(reread >= captured, $"{reread} messages to read again for {captured} captured payments");
        await AssertMoneyAddsUpAsync();
        Assert.Equal(postings, await PostingsAsync());
        Assert.Equal(balances, await AccountsAsync());

        output.WriteLine($"The ledger read {reread} messages again, all of them already booked: no posting or balance changed");
    }

    /// <summary>
    /// Sends the orders, 16 at a time. The copies of a duplicated order are sent all at once,
    /// the way a client retries when its first request seems to hang.
    /// </summary>
    private async Task<IReadOnlyCollection<Answer>> PayAllAsync(IReadOnlyCollection<Order> orders)
    {
        var answers = new ConcurrentBag<Answer>();
        var parallel = new ParallelOptions { MaxDegreeOfParallelism = Concurrency, CancellationToken = Ct };
        await Parallel.ForEachAsync(orders, parallel, async (order, ct) =>
        {
            foreach (var answer in await Task.WhenAll(Enumerable.Range(0, order.Copies).Select(_ => PayAsync(order, ct))))
                answers.Add(answer);
        });
        return answers;
    }

    /// <summary>No double charge: every order, however many copies, is one payment.</summary>
    private async Task AssertNoDoubleChargeAsync(IReadOnlyCollection<Order> orders, IReadOnlyCollection<Answer> answers)
    {
        foreach (var copies in answers.GroupBy(a => a.Order.Key))
        {
            Assert.Single(copies.Select(a => a.Payment.Id).Distinct());
            Assert.Single(copies, a => a.Status == HttpStatusCode.Created);   // the copy that won; the rest are replays
        }

        var keys = orders.Select(o => o.Key).ToArray();
        Assert.Equal(keys.Length, await PaymentsWithKeysAsync(keys));
    }

    /// <summary>
    /// Waits until the asynchronous part has caught up, then checks that no captured payment was lost or booked twice
    /// on the way and that the money adds up across the services. Returns the number of captured payments.
    /// </summary>
    private async Task<long> AssertMoneyAddsUpAsync()
    {
        // 1. Caught up: the outbox is empty, so every event is in Kafka, and after that the ledger has committed
        //    everything in Kafka; it commits a batch only after writing it. In this order: the other way round,
        //    the relay could still publish the last events in between.
        await Eventually.WaitUntilAsync(
            async () => await ScalarAsync(platform.PaymentsDb, "SELECT count(*) FROM outbox") == 0
                && await platform.LedgerLagAsync() == 0,
            timeout: TimeSpan.FromSeconds(60));

        // 2. Nothing is lost on the way: the ledger has every captured payment, and no payment is left unfinished.
        var captured = await CapturedAsync();
        Assert.Equal(captured, await BookedAsync());
        Assert.Equal(0, await ScalarAsync(platform.PaymentsDb, "SELECT count(*) FROM payments WHERE status = 'created'"));

        // 3. Money adds up across the services: what Payments.Api captured is what the ledger booked, once.
        var owedToMerchants = await BalancesAsync(platform.PaymentsDb, """
            SELECT 'merchant:' || merchant_id, sum(amount - fee)::bigint
            FROM payments WHERE status = 'captured' GROUP BY merchant_id
            """);
        var merchantAccounts = await BalancesAsync(platform.LedgerDb,
            "SELECT id, balance FROM accounts WHERE id LIKE 'merchant:%'");
        Assert.Equal(owedToMerchants, merchantAccounts);

        Assert.Equal(
            await ScalarAsync(platform.PaymentsDb, "SELECT coalesce(sum(fee), 0)::bigint FROM payments WHERE status = 'captured'"),
            await ScalarAsync(platform.LedgerDb, "SELECT coalesce(sum(balance), 0)::bigint FROM accounts WHERE id LIKE 'fee:%'"));
        Assert.Equal(
            -(await ScalarAsync(platform.PaymentsDb, "SELECT coalesce(sum(amount), 0)::bigint FROM payments WHERE status = 'captured'")),
            await ScalarAsync(platform.LedgerDb, "SELECT coalesce(sum(balance), 0)::bigint FROM accounts WHERE id LIKE 'clearing:%'"));

        // 4. And the ledger's own invariants: the check the load ladder prints after every step.
        var invariants = await platform.Ledger.GetFromJsonAsync<LedgerInvariants>("/invariants", Ct);
        Assert.NotNull(invariants);
        Assert.True(invariants.Ok, invariants.ToString());
        Assert.Equal(captured, invariants.Payments);

        return captured;
    }

    private async Task<Answer> PayAsync(Order order, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/payments")
        {
            Content = JsonContent.Create(new CreatePaymentRequest(order.MerchantId, order.Amount, "RUB")),
        };
        request.Headers.Add("Idempotency-Key", order.Key);

        using var response = await platform.Payments.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var problem = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException($"POST /payments answered {(int)response.StatusCode}: {problem}");
        }

        var payment = await response.Content.ReadFromJsonAsync<PaymentResponse>(cancellationToken);
        return new Answer(order, response.StatusCode, payment!);
    }

    private Task<long> CapturedAsync() =>
        ScalarAsync(platform.PaymentsDb, "SELECT count(*) FROM payments WHERE status = 'captured'");

    /// <summary>Payments the ledger has booked.</summary>
    private Task<long> BookedAsync() =>
        ScalarAsync(platform.LedgerDb, "SELECT count(*) FROM processed_payments");

    private Task<long> PostingsAsync() =>
        ScalarAsync(platform.LedgerDb, "SELECT count(*) FROM postings");

    private Task<SortedDictionary<string, long>> AccountsAsync() =>
        BalancesAsync(platform.LedgerDb, "SELECT id, balance FROM accounts");

    private async Task<long> PaymentsWithKeysAsync(string[] keys)
    {
        await using var command = platform.PaymentsDb.CreateCommand("SELECT count(*) FROM payments WHERE idempotency_key = ANY($1)");
        command.Parameters.Add(new NpgsqlParameter<string[]> { TypedValue = keys });
        return (long)(await command.ExecuteScalarAsync(Ct))!;
    }

    private static async Task<long> ScalarAsync(NpgsqlDataSource database, string sql)
    {
        await using var command = database.CreateCommand(sql);
        return (long)(await command.ExecuteScalarAsync(Ct))!;
    }

    private static async Task<SortedDictionary<string, long>> BalancesAsync(NpgsqlDataSource database, string sql)
    {
        var balances = new SortedDictionary<string, long>(StringComparer.Ordinal);
        await using var command = database.CreateCommand(sql);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
            balances.Add(reader.GetString(0), reader.GetInt64(1));
        return balances;
    }

    /// <summary>The orders of a test, shuffled: single ones, and 20 more that the client sends five times each.</summary>
    private static List<Order> NewOrders(Random random, int singles) =>
        Enumerable.Range(0, singles).Select(_ => NewOrder(random, copies: 1))
            .Concat(Enumerable.Range(0, DuplicatedOrders).Select(_ => NewOrder(random, copies: CopiesOfDuplicate)))
            .OrderBy(_ => random.Next())
            .ToList();

    private static Order NewOrder(Random random, int copies) =>
        new(Guid.NewGuid().ToString(), MerchantId: 1 + random.Next(Merchants), Amount: 1_000 + random.Next(99_001), copies);

    /// <summary>One payment a client wants made: a key, a body, and how many times the client sends it.</summary>
    private sealed record Order(string Key, long MerchantId, long Amount, int Copies);

    /// <summary>What the client got back for one copy of an order.</summary>
    private sealed record Answer(Order Order, HttpStatusCode Status, PaymentResponse Payment);
}
