using System.Diagnostics;

namespace SagaDrill;

/// <summary>What the phase found: the load, the moments of the stop, the payments it made, the counters, the checks.</summary>
/// <param name="MadeWhileDown">Payments made from <see cref="OutagePhase.ShutDown"/> after the stop to the return.</param>
public sealed record RedisResult(
    K6Result K6, OutageTimes Times, long FailOpenBelow, int Replicas, long MadeWhileDown, IReadOnlyDictionary<string, long> Statuses,
    SagaCounts Counts, LargePayments Large, TimeSpan? Settled, IReadOnlyList<Check> Checks) : IPhaseResult;

/// <summary>
/// The phase «Redis лежит» (stage 4, task 7): under load every copy of Redis goes away, Sentinels and all, and comes
/// back. On the stand one Redis serves both the antifraud, which keeps its counters and decisions there and answers
/// 503 without it, and the merchant rate limiter of Payments.Api, which falls back to a local limit of each pod
/// (RedisCircuitBreaker, stage 3). So payments keep coming in; a small one is captured unchecked, a large one waits in
/// 'authorized' until recovery gets a decision once Redis is back. The alerts of the saga are not checked: they need
/// 5 minutes, and the outage phase checks them.
/// </summary>
public static class RedisPhase
{
    private const string Redis = "redis";

    // At least this share of the payments k6 sends while Redis is away must be made: the rate limiter lets them through
    // by its local limit, far above the pace of a merchant of the mix.
    private const double MadeShare = 0.9;

    public static async Task<RedisResult> RunAsync(DrillOptions options, Prometheus prometheus, string reports, CancellationToken ct)
    {
        var limit = TimeSpan.FromSeconds(options.SettleSeconds);
        var failOpenBelow = OutagePhase.FailOpenBelow();
        if (failOpenBelow == 0)
            throw new DrillException("Antifraud:FailOpenBelowAmount is 0 on the stand: fail open is off, and the Redis phase " +
                "checks what the saga does with it");

        Console.WriteLine("Redis phase: waiting for the payments before it to finish");
        var (before, loadStart) = await Phase.BeginAsync(prometheus, limit, ct);
        var (redis, error) = await Stand.StatefulSetAsync(Redis);
        if (redis is not { Desired: > 0 } || redis.Ready != redis.Desired)
            throw new DrillException($"Redis is not ready to be stopped: {(redis is null ? error : $"{redis.Ready} of {redis.Desired}")}");
        var autoSync = await Stand.AutoSyncAsync()
            ?? throw new DrillException("The auto-sync of Argo CD is off already: put it back, kubectl apply -f deploy/argocd/highload.yaml");

        Console.WriteLine($"Redis phase: {options.Rate} payments a second for {options.BeforeSeconds + options.RedisDownSeconds + options.AfterSeconds} s, " +
            $"Redis away from {options.BeforeSeconds} s to {options.BeforeSeconds + options.RedisDownSeconds} s");
        // Argo CD would put the replicas back within seconds: selfHeal is on.
        await Stand.SetAutoSyncAsync(null);
        OutageTimes times;
        K6Result k6;
        try
        {
            (times, k6) = await LoadAsync(options, loadStart, redis.Desired, autoSync, limit, reports, ct);
        }
        catch
        {
            // Ctrl+C, or a step that failed halfway: the stand goes back as it was all the same, before anything else.
            await RestoreAsync(redis.Desired, autoSync);
            throw;
        }

        Console.WriteLine("Redis phase: waiting for every payment to get its final status");
        var (settled, after) = await Phase.EndAsync(prometheus, limit, ct);
        var counts = SagaCounts.Between(before, after);
        var (argo, argoError) = await OutagePhase.ArgoBackAsync(limit, ct);

        var statuses = await Phase.StatusesAsync(times.LoadStart, times.LoadEnd);
        var large = await OutagePhase.LargeAsync(times, failOpenBelow);
        var madeWhileDown = await Postgres.NumberAsync("payments",
            $"select count(*) from payments where {Phase.Window(times.Stop + OutagePhase.ShutDown.TotalSeconds, times.Back)}")
            ?? throw new DrillException("Cannot count the payments made while Redis was away in PostgreSQL");
        var expected = options.Rate * (times.Back - times.Stop - OutagePhase.ShutDown.TotalSeconds);

        var checks = new List<Check>
        {
            new("без Redis платежи принимались: лимитер перешёл на локальный лимит",
                madeWhileDown >= MadeShare * expected,
                $"пока Redis не было, создано {madeWhileDown} платежей из примерно {Phase.N(expected)}, что k6 отправил за это время"),
            new($"без Redis мелкие платежи проходили без проверки, от {OutagePhase.Roubles(failOpenBelow)} ждали антифрод",
                counts.FailOpen > 0 && counts.WaitedInRequest > 0 && large.CapturedBeforeBack == 0,
                $"без проверки проведено {Phase.N(counts.FailOpen)}, ждали в `authorized` {Phase.N(counts.WaitedInRequest)}; " +
                $"из {large.Made} платежей от {OutagePhase.Roubles(failOpenBelow)}, созданных без Redis, до его возвращения проведено " +
                $"{large.CapturedBeforeBack}"),
            Phase.Settled(statuses, settled, limit),
        };
        checks.AddRange(Phase.Books(counts, statuses));
        checks.Add(await Phase.LedgerAsync(DenyPhase.HotMerchant, limit, ct));
        checks.Add(await Phase.InvariantsAsync());
        checks.Add(StandBack(times, redis.Desired, argo, argoError, limit));

        return new RedisResult(k6, times, failOpenBelow, redis.Desired, madeWhileDown, statuses, counts, large, settled, checks);
    }

    // The load runs from end to end, Redis stopped and started under it. The stand is put back as soon as Redis is
    // ready again: the rest of the load runs with Argo CD watching.
    private static async Task<(OutageTimes, K6Result)> LoadAsync(
        DrillOptions options, double loadStart, int replicas, string autoSync, TimeSpan limit, string reports, CancellationToken ct)
    {
        using var stopLoad = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var load = K6.RunAsync(options.Rate, options.BeforeSeconds + options.RedisDownSeconds + options.AfterSeconds, reports, "redis", stopLoad.Token);
        try
        {
            await OutagePhase.DuringLoadAsync(load, TimeSpan.FromSeconds(options.BeforeSeconds), ct);
            await Stand.ScaleAsync(Redis, 0, "statefulset");
            var down = Stopwatch.StartNew();
            var stop = await Postgres.ClockAsync();
            Console.WriteLine($"Redis phase: Redis is stopped, back in {options.RedisDownSeconds} s. Ctrl+C puts the stand back; " +
                $"if the tool is killed, put it back by hand: kubectl -n {Stand.Namespace} scale statefulset/{Redis} --replicas={replicas}; " +
                "kubectl apply -f deploy/argocd/highload.yaml");

            await OutagePhase.DuringLoadAsync(load, TimeSpan.FromSeconds(options.RedisDownSeconds) - down.Elapsed, ct);
            await Stand.ScaleAsync(Redis, replicas, "statefulset");
            var back = await Postgres.ClockAsync();
            Console.WriteLine("Redis phase: Redis is back, waiting for its replicas to be ready");
            var ready = await ReadyAsync(replicas, limit, ct);
            await Stand.SetAutoSyncAsync(autoSync);
            Console.WriteLine("Redis phase: the auto-sync of Argo CD is back on; the load goes on");

            var k6 = await load;
            return (new OutageTimes(loadStart, stop, back, ready, await Postgres.ClockAsync()), k6);
        }
        finally
        {
            if (!load.IsCompleted)
            {
                stopLoad.Cancel();
                try
                {
                    await load;
                }
                catch (OperationCanceledException)
                {
                }
            }
        }
    }

    // When every copy of Redis is ready again, by the clock of the database; null if not within the limit. A copy is
    // ready once Redis and its Sentinel answer PING (redis.yaml). That says nothing of a master: until the Sentinels
    // agree on one the antifraud still answers 503, and the large payments wait a little longer, which the checks allow.
    private static async Task<double?> ReadyAsync(int replicas, TimeSpan limit, CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < limit)
        {
            if (await Stand.StatefulSetAsync(Redis) is ({ } now, _) && now.Ready == replicas)
                return await Postgres.ClockAsync();
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }
        return null;
    }

    // Never throws, and says plainly when it fails: a stand left with no Redis and no auto-sync fails every preflight after it.
    private static async Task RestoreAsync(int replicas, string autoSync)
    {
        var failed = new List<string>();
        foreach (var step in new Func<Task>[] { () => Stand.ScaleAsync(Redis, replicas, "statefulset"), () => Stand.SetAutoSyncAsync(autoSync) })
        {
            try
            {
                await step();
            }
            catch (DrillException e)
            {
                failed.Add(e.Message);
            }
        }
        if (failed.Count == 0)
            Console.WriteLine($"Redis phase: the stand is put back: {replicas} copies of Redis, the auto-sync of Argo CD on");
        else
            Console.Error.WriteLine($"Redis phase: the stand is NOT put back ({string.Join("; ", failed)}). By hand: " +
                $"kubectl -n {Stand.Namespace} scale statefulset/{Redis} --replicas={replicas}; kubectl apply -f deploy/argocd/highload.yaml");
    }

    private static Check StandBack(OutageTimes times, int replicas, ArgoApp? argo, string argoError, TimeSpan limit)
    {
        var ok = times.Ready is not null && argo is { Sync: "Synced", Health: "Healthy", SelfHeal: true };
        var redis = times.Ready is { } ready
            ? $"Redis готов через {ready - times.Back:0} с после возвращения, {replicas} из {replicas}"
            : $"копии Redis не готовы и через {limit.TotalSeconds:0} с после возвращения";
        var app = argo is null
            ? $"приложение Argo CD не прочитать: {argoError}"
            : $"Argo CD: {argo.Sync}, {argo.Health}, selfHeal {(argo.SelfHeal ? "включён" : "выключен")}";
        return new Check("стенд вернулся как был: Redis готов, Argo CD синхронизирован и сам возвращает ручные правки", ok, $"{redis}; {app}");
    }
}
