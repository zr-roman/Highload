namespace SagaDrill;

/// <summary>The command line of the tool.</summary>
public sealed record DrillOptions(int Rate, int WarmupSeconds, string? PrometheusUrl)
{
    public const string Usage = """
        The saga on the k3d stand when the antifraud fails (stage 4). For now the preflight: the stand is ready for
        the drill, Prometheus has loaded the rules of highload.saga, and under a warm-up load the antifraud answers
        and every query of the saga row of the dashboard and of the saga's alerts finds its metrics. Writes
        load/reports/saga-drill-<time>/report.md.

        From the repository root, with the cluster up and no other load:
          dotnet run --project load\SagaDrill -c Release
          dotnet run --project load\SagaDrill -c Release -- --rate 50 --warmup 60
          dotnet run --project load\SagaDrill -c Release -- --prometheus http://localhost:9090

          --rate R        payments a second of the warm-up load (default: 50)
          --warmup S      seconds of the warm-up load before the queries, 30 or more (default: 150). HPA adds
                          Payments.Api pods within the first minute of load, and they start cold; the checks look
                          at the last minute, after that. The latency of the antifraud is a histogram: it has no
                          series until the first check, and a rate needs two of its exports, 15 s apart
          --prometheus U  query Prometheus at this address, a port-forward of your own, instead of opening one
        """;

    public static DrillOptions Parse(string[] args)
    {
        var options = new DrillOptions(50, 150, null);
        for (var i = 0; i < args.Length; i++)
        {
            var name = args[i];
            if (i + 1 >= args.Length)
                throw new DrillException($"{name} needs a value. {Usage}");
            var value = args[++i];
            options = name switch
            {
                "--rate" => options with { Rate = Positive(name, value) },
                "--warmup" => options with { WarmupSeconds = Positive(name, value) },
                "--prometheus" => options with { PrometheusUrl = Address(value) },
                _ => throw new DrillException($"Unknown option {name}. {Usage}"),
            };
        }

        if (options.WarmupSeconds < 30)
            throw new DrillException($"--warmup must be 30 or more, got {options.WarmupSeconds}: a shorter load leaves the " +
                "latency histogram of the antifraud with less than two exports in the minute of the query");
        return options;
    }

    private static int Positive(string name, string value) =>
        int.TryParse(value, out var number) && number > 0 ? number : throw new DrillException($"{name} must be a positive number, got {value}");

    private static string Address(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"
            ? value.TrimEnd('/')
            : throw new DrillException($"--prometheus must be an http address like http://localhost:9090, got {value}");
}
