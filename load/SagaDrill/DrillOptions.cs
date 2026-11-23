namespace SagaDrill;

/// <summary>What a run does: every phase, the preflight alone, or the preflight and one phase.</summary>
public enum Part
{
    All,
    Preflight,
    Deny,
    Outage,
    Redis,
    Kill,
}

/// <summary>The command line of the tool.</summary>
public sealed record DrillOptions(
    int Rate, int WarmupSeconds, int DenySeconds, int BeforeSeconds, int DownSeconds, int AfterSeconds, int SettleSeconds,
    string? PrometheusUrl, Part Only = Part.All)
{
    /// <summary>Redis phase: seconds every copy of Redis is away.</summary>
    public int RedisDownSeconds { get; init; } = 120;

    /// <summary>Kill phase: seconds of load; every pod of Payments.Api is killed in its middle.</summary>
    public int KillLoadSeconds { get; init; } = 180;

    // Below this the kill phase has no load on either side of the kill: new pods take 20-40 s to be ready.
    private const int ShortestKillLoad = 120;

    // Below this the stop of Redis is over before its pods have gone: they end within ShutDown.
    private const int ShortestRedisDown = 60;

    /// <summary>
    /// The shortest outage in which PaymentsStuckInAuthorized can fire: the oldest payment in 'authorized' is older
    /// than 300 s on the first export after that, at most 15 s later; the rule sees it at its next evaluation, at most
    /// 30 s later, and fires 30 s on (for), at the evaluation after that: 375 s after the stop at the latest. The rest
    /// is a margin for the collector and the kubectl of the stop. AntifraudFailingOpen fires about as late: 5 minutes
    /// after the first evaluation that finds more than 5 % of the checks failing open, within a minute of the stop.
    /// </summary>
    public const int ShortestOutage = 420;

    public const string Usage = """
        The saga on the k3d stand when the antifraud fails (stage 4), in phases. Writes
        load/reports/saga-drill-<time>/report.md.
          preflight  the stand is ready for the drill, one payment goes through the whole path (deploy/smoke.ps1),
                     Prometheus has loaded the rules of highload.saga, and under a warm-up load the antifraud
                     answers and every query of the saga row of the dashboard and of the saga's alerts finds its
                     metrics
          deny       the antifraud says no, the saga voids: every deny ends in a void, no payment is left in
                     progress, the Ledger gets the captured payments alone
          outage     the antifraud goes away under load (kubectl scale --replicas=0, with the auto-sync of Argo CD off
                     meanwhile) and comes back: payments below Antifraud:FailOpenBelowAmount are captured unchecked,
                     the others wait in 'authorized' and are captured or voided once it is back;
                     PaymentsStuckInAuthorized and AntifraudFailingOpen fire while it is away and resolve after, and
                     Alertmanager sends them to Telegram
          redis      every copy of Redis goes away under load (kubectl scale statefulset/redis --replicas=0, the
                     auto-sync of Argo CD off meanwhile) and comes back: the rate limiter of Payments.Api falls back
                     to its local limit and payments keep coming in, the antifraud answers 503 and the saga does
                     what it does in the outage phase, the large payments are finished once Redis is back
          kill       the antifraud denies every payment (ANTIFRAUD_VELOCITY_LIMIT=0 by kubectl set env) and in the
                     middle of the load every pod of Payments.Api is killed at once, without a grace period:
                     no payment from Antifraud:FailOpenBelowAmount is captured, recovery voids the payments the
                     kill cut off, and the bank emulator has the voids of a sample of them
        The phases run in this order, each only if everything before it passed. With the defaults all of them
        take about 35 minutes, the outage phase 15 of them.

        From the repository root, with the cluster up and no other load:
          dotnet run --project load\SagaDrill -c Release
          dotnet run --project load\SagaDrill -c Release -- --only preflight
          dotnet run --project load\SagaDrill -c Release -- --only outage --down 480
          dotnet run --project load\SagaDrill -c Release -- --only redis
          dotnet run --project load\SagaDrill -c Release -- --only kill
          dotnet run --project load\SagaDrill -c Release -- --prometheus http://localhost:9090

          --only P          the preflight alone (preflight), or the preflight and one phase (deny, outage,
                            redis, kill)
          --rate R          payments a second of every phase, 20 or more for the deny phase (default: 50). The denies
                            come from merchant 1, who gets about 10 % of the payments of load/payments.js: below 20 a
                            second he stays under the 100 a minute the velocity rule lets through
          --warmup S        seconds of the warm-up load before the queries, 30 or more (default: 150). HPA adds
                            Payments.Api pods within the first minute of load, and they start cold; the checks look
                            at the last minute, after that. The latency of the antifraud is a histogram: it has no
                            series until the first check, and a rate needs two of its exports, 15 s apart
          --deny S          seconds of load of the deny phase (default: 180)
          --before S        outage and redis phases: seconds of load before the stop (default: 60)
          --down S          outage phase: seconds the antifraud is away, 420 or more (default: 450).
                            PaymentsStuckInAuthorized fires up to 375 s after the stop: 300 s of waiting, 30 s of
                            for, the export of the metrics and the evaluation of the rules
          --after S         outage and redis phases: seconds of load once it is back (default: 240). Recovery
                            takes up the payments that waited, and AntifraudFailingOpen resolves once fewer than
                            5 % of the checks of its 2-minute window fail open
          --redis-down S    redis phase: seconds Redis is away, 60 or more (default: 120)
          --kill-load S     kill phase: seconds of load, 120 or more, the kill in the middle (default: 180)
          --settle S        after a load, how long to wait for every payment to get its final status, for the
                            Ledger to catch up, for the alerts to resolve and for Argo CD to sync (default: 120).
                            Recovery takes a payment up after 30 s
          --prometheus U    query Prometheus at this address, a port-forward of your own, instead of opening one
        """;

    public bool Runs(Part phase) => Only == Part.All || Only == phase;

    public static DrillOptions Parse(string[] args)
    {
        var options = new DrillOptions(50, 150, 180, 60, 450, 240, 120, null);
        for (var i = 0; i < args.Length; i++)
        {
            var name = args[i];
            if (name == "--preflight-only")
                throw new DrillException("--preflight-only is --only preflight now");
            if (i + 1 >= args.Length)
                throw new DrillException($"{name} needs a value. {Usage}");
            var value = args[++i];
            options = name switch
            {
                "--only" => options with { Only = Phase(value) },
                "--rate" => options with { Rate = Positive(name, value) },
                "--warmup" => options with { WarmupSeconds = Positive(name, value) },
                "--deny" => options with { DenySeconds = Positive(name, value) },
                "--before" => options with { BeforeSeconds = Positive(name, value) },
                "--down" => options with { DownSeconds = Positive(name, value) },
                "--after" => options with { AfterSeconds = Positive(name, value) },
                "--redis-down" => options with { RedisDownSeconds = Positive(name, value) },
                "--kill-load" => options with { KillLoadSeconds = Positive(name, value) },
                "--settle" => options with { SettleSeconds = Positive(name, value) },
                "--prometheus" => options with { PrometheusUrl = Address(value) },
                _ => throw new DrillException($"Unknown option {name}. {Usage}"),
            };
        }

        if (options.WarmupSeconds < 30)
            throw new DrillException($"--warmup must be 30 or more, got {options.WarmupSeconds}: a shorter load leaves the " +
                "latency histogram of the antifraud with less than two exports in the minute of the query");
        if (options.Rate < 20 && options.Runs(Part.Deny))
            throw new DrillException($"--rate must be 20 or more for the deny phase, got {options.Rate}: merchant 1 gets about " +
                "10 % of the payments, and below 100 a minute the velocity rule denies none of them");
        if (options.DownSeconds < ShortestOutage && options.Runs(Part.Outage))
            throw new DrillException($"--down must be {ShortestOutage} or more, got {options.DownSeconds}: PaymentsStuckInAuthorized " +
                "fires up to 375 s after the stop, and the drill checks that it fires while the antifraud is away");
        if (options.RedisDownSeconds < ShortestRedisDown && options.Runs(Part.Redis))
            throw new DrillException($"--redis-down must be {ShortestRedisDown} or more, got {options.RedisDownSeconds}: " +
                "the phase counts the payments made from the moment the pods of Redis are gone");
        if (options.KillLoadSeconds < ShortestKillLoad && options.Runs(Part.Kill))
            throw new DrillException($"--kill-load must be {ShortestKillLoad} or more, got {options.KillLoadSeconds}: " +
                "the new pods of Payments.Api take 20-40 s to be ready, and the load has to go on after them");
        return options;
    }

    private static Part Phase(string value) => value switch
    {
        "preflight" => Part.Preflight,
        "deny" => Part.Deny,
        "outage" => Part.Outage,
        "redis" => Part.Redis,
        "kill" => Part.Kill,
        _ => throw new DrillException($"--only must be preflight, deny, outage, redis or kill, got {value}"),
    };

    private static int Positive(string name, string value) =>
        int.TryParse(value, out var number) && number > 0 ? number : throw new DrillException($"{name} must be a positive number, got {value}");

    private static string Address(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"
            ? value.TrimEnd('/')
            : throw new DrillException($"--prometheus must be an http address like http://localhost:9090, got {value}");
}
