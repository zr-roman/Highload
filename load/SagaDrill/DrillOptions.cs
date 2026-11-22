namespace SagaDrill;

/// <summary>The command line of the tool.</summary>
public sealed record DrillOptions(
    int Rate, int WarmupSeconds, int DenySeconds, int SettleSeconds, string? PrometheusUrl, bool PreflightOnly = false)
{
    public const string Usage = """
        The saga on the k3d stand when the antifraud fails (stage 4), in phases. Writes
        load/reports/saga-drill-<time>/report.md.
          preflight  the stand is ready for the drill, Prometheus has loaded the rules of highload.saga, and under a
                     warm-up load the antifraud answers and every query of the saga row of the dashboard and of the
                     saga's alerts finds its metrics
          deny       the antifraud says no, the saga voids: every deny ends in a void, no payment is left in
                     progress, the Ledger gets the captured payments alone. Runs only if the preflight passed

        From the repository root, with the cluster up and no other load:
          dotnet run --project load\SagaDrill -c Release
          dotnet run --project load\SagaDrill -c Release -- --preflight-only
          dotnet run --project load\SagaDrill -c Release -- --rate 50 --warmup 150 --deny 180
          dotnet run --project load\SagaDrill -c Release -- --prometheus http://localhost:9090

          --rate R          payments a second of every phase, 20 or more (default: 50). The denies come from merchant 1,
                            who gets about 10 % of the payments of load/payments.js: below 20 a second he stays under
                            the 100 a minute the velocity rule lets through
          --warmup S        seconds of the warm-up load before the queries, 30 or more (default: 150). HPA adds
                            Payments.Api pods within the first minute of load, and they start cold; the checks look
                            at the last minute, after that. The latency of the antifraud is a histogram: it has no
                            series until the first check, and a rate needs two of its exports, 15 s apart
          --deny S          seconds of load of the deny phase (default: 180)
          --settle S        after a load, how long to wait for every payment to get its final status, and for the
                            Ledger to catch up (default: 120). Recovery takes a payment up after 30 s
          --preflight-only  the preflight alone
          --prometheus U    query Prometheus at this address, a port-forward of your own, instead of opening one
        """;

    public static DrillOptions Parse(string[] args)
    {
        var options = new DrillOptions(50, 150, 180, 120, null);
        for (var i = 0; i < args.Length; i++)
        {
            var name = args[i];
            if (name == "--preflight-only")
            {
                options = options with { PreflightOnly = true };
                continue;
            }
            if (i + 1 >= args.Length)
                throw new DrillException($"{name} needs a value. {Usage}");
            var value = args[++i];
            options = name switch
            {
                "--rate" => options with { Rate = Positive(name, value) },
                "--warmup" => options with { WarmupSeconds = Positive(name, value) },
                "--deny" => options with { DenySeconds = Positive(name, value) },
                "--settle" => options with { SettleSeconds = Positive(name, value) },
                "--prometheus" => options with { PrometheusUrl = Address(value) },
                _ => throw new DrillException($"Unknown option {name}. {Usage}"),
            };
        }

        if (options.WarmupSeconds < 30)
            throw new DrillException($"--warmup must be 30 or more, got {options.WarmupSeconds}: a shorter load leaves the " +
                "latency histogram of the antifraud with less than two exports in the minute of the query");
        if (options.Rate < 20 && !options.PreflightOnly)
            throw new DrillException($"--rate must be 20 or more for the deny phase, got {options.Rate}: merchant 1 gets about " +
                "10 % of the payments, and below 100 a minute the velocity rule denies none of them");
        return options;
    }

    private static int Positive(string name, string value) =>
        int.TryParse(value, out var number) && number > 0 ? number : throw new DrillException($"{name} must be a positive number, got {value}");

    private static string Address(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"
            ? value.TrimEnd('/')
            : throw new DrillException($"--prometheus must be an http address like http://localhost:9090, got {value}");
}
