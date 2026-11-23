using System.Diagnostics;
using System.Globalization;

namespace SagaDrill;

/// <summary>The moments of the phase, in seconds since 1970 by the clock of the cluster.</summary>
/// <param name="ApiReady">When every replica of Payments.Api was ready again after the kill; null if not within the limit.</param>
public sealed record KillTimes(double LoadStart, double Killed, double? ApiReady, double LoadEnd);

/// <summary>The payments of the phase by what became of them, from the database.</summary>
/// <param name="CapturedLarge">Captured from Antifraud:FailOpenBelowAmount up: the antifraud allowed them, which it must not.</param>
/// <param name="CapturedSmall">Captured below it: unchecked (fail open), as while the new pods were cold.</param>
/// <param name="VoidedByRecovery">Voided StaleAfter or later after they were made: recovery took them up, the request did not finish them.</param>
public sealed record KilledPayments(long CapturedLarge, long CapturedSmall, long Voided, long VoidedByRecovery);

/// <summary>What the phase found.</summary>
/// <param name="RecoveryVoided">payments.voided of recovery by the counters: less what the killed pods had not exported.</param>
public sealed record KillResult(
    K6Result K6, KillTimes Times, long FailOpenBelow, int ApiReplicas, IReadOnlyDictionary<string, long> Statuses, KilledPayments Payments,
    double RecoveryVoided, BankSample? Bank, TimeSpan? Settled, IReadOnlyList<Check> Checks) : IPhaseResult;

/// <summary>
/// The phase «всё отклоняется, Payments.Api убит» (stage 4, task 7): two items of the plan in one load. The antifraud
/// gets a velocity limit of 0 and denies every payment, so every approved one has to be voided at the bank; in the
/// middle of the load every pod of Payments.Api is killed at once, without the grace period. The sagas the pods were
/// running stop wherever they were: before the bank, between its approval and the void, before the status was
/// written. Recovery takes them up after StaleAfter and must void every one the bank approved. A kill between
/// approval and void exactly is a matter of luck at this pace; the phase checks that every payment the kill cut off
/// ends voided, whatever step it stopped at.
/// </summary>
/// <remarks>
/// The metrics of the killed pods are lost with them: whatever they counted since their last export is gone. So the
/// books of the saga are not compared here; the database and the bank tell what happened.
/// </remarks>
public static class KillPhase
{
    private const string Antifraud = "antifraud";
    private const string PaymentsApi = "payments-api";
    private const string VelocityLimit = "ANTIFRAUD_VELOCITY_LIMIT";

    // Voided payments the bank is asked about: enough to see a pattern, few enough for a port-forward one by one.
    private const int BankSampleSize = 200;

    private static readonly TimeSpan Rollout = TimeSpan.FromSeconds(180);

    public static async Task<KillResult> RunAsync(DrillOptions options, Prometheus prometheus, string reports, CancellationToken ct)
    {
        var limit = TimeSpan.FromSeconds(options.SettleSeconds);
        var failOpenBelow = OutagePhase.FailOpenBelow();
        var staleAfter = OutagePhase.Recovery("StaleAfter");

        Console.WriteLine("Kill phase: waiting for the payments before it to finish");
        var (before, loadStart) = await Phase.BeginAsync(prometheus, limit, ct);
        var (api, error) = await Stand.DeploymentAsync(PaymentsApi);
        if (api is not { Desired: > 0 })
            throw new DrillException($"Payments.Api has no replicas to kill: {(api is null ? error : "0 of 0")}");
        if (await Stand.EnvAsync(Antifraud, VelocityLimit) is { } set)
            throw new DrillException($"deployment/{Antifraud} has {VelocityLimit}={set} already: the phase sets it and removes it after. " +
                $"Is it left from a run that was killed? kubectl -n {Stand.Namespace} set env deployment/{Antifraud} {VelocityLimit}-");
        var autoSync = await Stand.AutoSyncAsync()
            ?? throw new DrillException("The auto-sync of Argo CD is off already: put it back, kubectl apply -f deploy/argocd/highload.yaml");

        // Argo CD would undo the variable within seconds: selfHeal is on.
        await Stand.SetAutoSyncAsync(null);
        KillTimes times;
        K6Result k6;
        try
        {
            Console.WriteLine($"Kill phase: the antifraud denies every payment ({VelocityLimit}=0), waiting for its new pods");
            await Stand.SetEnvAsync(Antifraud, $"{VelocityLimit}=0");
            await Stand.RolloutAsync(Antifraud, Rollout);
            // The phase starts on new pods of the antifraud: the payments it counts are those made from here.
            loadStart = await Postgres.ClockAsync();
            (times, k6) = await LoadAsync(options, loadStart, api.Desired, limit, reports, ct);

            Console.WriteLine("Kill phase: the velocity limit of the antifraud is taken back, waiting for its new pods");
            await Stand.SetEnvAsync(Antifraud, $"{VelocityLimit}-");
            await Stand.RolloutAsync(Antifraud, Rollout);
            await Stand.SetAutoSyncAsync(autoSync);
        }
        catch
        {
            await RestoreAsync(autoSync);
            throw;
        }

        Console.WriteLine("Kill phase: waiting for every payment to get its final status");
        var (settled, after) = await Phase.EndAsync(prometheus, limit, ct);
        var recoveryVoided = after.Since(before, "voided", ("path", "recovery"));
        var (argo, argoError) = await OutagePhase.ArgoBackAsync(limit, ct);
        var restored = await Stand.EnvAsync(Antifraud, VelocityLimit);

        var statuses = await Phase.StatusesAsync(times.LoadStart, times.LoadEnd);
        var payments = await PaymentsAsync(times, failOpenBelow, staleAfter);
        var voidedIds = (await Postgres.QueryAsync("payments",
                $"select id from payments where {Phase.Window(times.LoadStart, times.LoadEnd)} and status = 'voided' order by random() limit {BankSampleSize}") ?? [])
            .Select(r => r[0])
            .ToList();
        Console.WriteLine($"Kill phase: asking the bank emulator about {voidedIds.Count} voided payments");
        var bank = voidedIds.Count > 0 ? await Bank.StatusesAsync(voidedIds, reports, ct) : null;

        var checks = new List<Check>
        {
            new("Payments.Api убит и поднялся", times.ApiReady is not null,
                times.ApiReady is { } ready
                    ? $"все {api.Desired} подов убиты без grace period, новые готовы через {ready - times.Killed:0} с"
                    : $"новые поды не готовы и через {limit.TotalSeconds:0} с"),
            new($"антифрод отклонял всё: ни одного проведённого платежа от {OutagePhase.Roubles(failOpenBelow)}",
                payments.CapturedLarge == 0 && payments.Voided > 0,
                $"проведено от {OutagePhase.Roubles(failOpenBelow)} — {payments.CapturedLarge}, отменено {payments.Voided}; " +
                $"мелких проведено без проверки — {payments.CapturedSmall}"),
            new("банк подтверждает отмены", bank is { } b && b.Voided == b.Asked,
                bank is null
                    ? "отменённых платежей нет, спрашивать банк не о чем"
                    : $"из {bank.Asked} случайных отменённых платежей фазы у эмулятора банка в `voided` — {bank.Voided}" +
                      (bank.Voided == bank.Asked ? "" : $"; остальные: {string.Join(", ", bank.ByStatus.Where(s => s.Key != "voided").Select(s => $"{s.Key} — {s.Value}"))}")),
            new("восстановление довело брошенные платежи до отмены", payments.VoidedByRecovery > 0,
                $"отменены восстановлением (через {staleAfter.TotalSeconds:0} с и позже после создания) — {payments.VoidedByRecovery}; " +
                $"`payments.voided` восстановления по счётчикам — {Phase.N(recoveryVoided)}, без того, что убитые поды не успели отдать"),
            Phase.Settled(statuses, settled, limit),
            await Phase.LedgerAsync(DenyPhase.HotMerchant, limit, ct),
            await Phase.InvariantsAsync(),
            StandBack(restored, argo, argoError),
        };

        return new KillResult(k6, times, failOpenBelow, api.Desired, statuses, payments, recoveryVoided, bank, settled, checks);
    }

    // The load runs from end to end; in its middle every pod of Payments.Api is killed. Traefik answers k6 with errors
    // until the new pods are ready: the report has them.
    private static async Task<(KillTimes, K6Result)> LoadAsync(
        DrillOptions options, double loadStart, int replicas, TimeSpan limit, string reports, CancellationToken ct)
    {
        Console.WriteLine($"Kill phase: {options.Rate} payments a second for {options.KillLoadSeconds} s, " +
            $"every pod of Payments.Api killed at {options.KillLoadSeconds / 2} s");
        using var stopLoad = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var load = K6.RunAsync(options.Rate, options.KillLoadSeconds, reports, "kill", stopLoad.Token);
        try
        {
            await OutagePhase.DuringLoadAsync(load, TimeSpan.FromSeconds(options.KillLoadSeconds / 2), ct);
            await Stand.KillPodsAsync(PaymentsApi);
            var killed = await Postgres.ClockAsync();
            Console.WriteLine("Kill phase: every pod of Payments.Api is killed, waiting for the new ones");
            var ready = await ApiReadyAsync(replicas, limit, ct);

            var k6 = await load;
            return (new KillTimes(loadStart, killed, ready, await Postgres.ClockAsync()), k6);
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

    // Ready right after the kill means the old pods still counted: they leave the Deployment's count only once
    // deleted. The new ones are waited for by their age as well: every ready pod younger than the kill.
    private static async Task<double?> ApiReadyAsync(int replicas, TimeSpan limit, CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();
        await Task.Delay(TimeSpan.FromSeconds(5), ct);
        while (clock.Elapsed < limit)
        {
            if (await Stand.DeploymentAsync(PaymentsApi) is ({ } now, _) && now.Ready >= replicas)
                return await Postgres.ClockAsync();
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }
        return null;
    }

    private static async Task<KilledPayments> PaymentsAsync(KillTimes times, long failOpenBelow, TimeSpan staleAfter)
    {
        var stale = staleAfter.TotalSeconds.ToString("0", CultureInfo.InvariantCulture);
        var rows = await Postgres.QueryAsync("payments",
            $"select count(*) filter (where status = 'captured' and amount >= {failOpenBelow}), " +
            $"count(*) filter (where status = 'captured' and amount < {failOpenBelow}), " +
            "count(*) filter (where status = 'voided'), " +
            $"count(*) filter (where status = 'voided' and updated_at >= created_at + interval '{stale} seconds') " +
            $"from payments where {Phase.Window(times.LoadStart, times.LoadEnd)}");
        if (rows is not [{ Length: 4 } row])
            throw new DrillException("Cannot count the payments of the kill phase in PostgreSQL");
        var n = row.Select(v => long.Parse(v, CultureInfo.InvariantCulture)).ToArray();
        return new KilledPayments(n[0], n[1], n[2], n[3]);
    }

    // Never throws, and says plainly when it fails: an antifraud left denying everything fails every phase after it.
    private static async Task RestoreAsync(string autoSync)
    {
        var failed = new List<string>();
        foreach (var step in new Func<Task>[] { () => Stand.SetEnvAsync(Antifraud, $"{VelocityLimit}-"), () => Stand.SetAutoSyncAsync(autoSync) })
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
            Console.WriteLine($"Kill phase: the stand is put back: {VelocityLimit} removed from the antifraud, the auto-sync of Argo CD on");
        else
            Console.Error.WriteLine($"Kill phase: the stand is NOT put back ({string.Join("; ", failed)}). By hand: " +
                $"kubectl -n {Stand.Namespace} set env deployment/{Antifraud} {VelocityLimit}-; kubectl apply -f deploy/argocd/highload.yaml");
    }

    private static Check StandBack(string? velocity, ArgoApp? argo, string argoError)
    {
        var ok = velocity is null && argo is { Sync: "Synced", Health: "Healthy", SelfHeal: true };
        var antifraud = velocity is null ? $"у антифрода нет {VelocityLimit}, лимит снова из настроек" : $"у антифрода осталось {VelocityLimit}={velocity}";
        var app = argo is null
            ? $"приложение Argo CD не прочитать: {argoError}"
            : $"Argo CD: {argo.Sync}, {argo.Health}, selfHeal {(argo.SelfHeal ? "включён" : "выключен")}";
        return new Check("стенд вернулся как был: антифрод с прежним лимитом, Argo CD синхронизирован и сам возвращает ручные правки", ok,
            $"{antifraud}; {app}");
    }
}
