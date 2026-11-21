using System.Globalization;

namespace AntifraudBench;

/// <summary>The command line of the bench.</summary>
public sealed record BenchOptions(
    IReadOnlyList<int> Workers, IReadOnlyList<int> Rates, int Repeats, int DurationSeconds, int WarmupSeconds,
    int RedisTimeoutMs, double Cpus, string? K6Image, bool Build)
{
    public const string Usage = """
        Antifraud bench: runs the antifraud service in a Linux container next to Redis, with every number of uvicorn
        processes, loads it with k6 from a container at every rate and writes load/reports/antifraud-bench-<time>/table.md.

        From the repository root, with Docker Desktop running:
          dotnet run --project load\AntifraudBench -c Release
          dotnet run --project load\AntifraudBench -c Release -- --workers 1 --rates 200,400,600 --repeats 1
          dotnet run --project load\AntifraudBench -c Release -- --workers 4 --rates 1000 --repeats 3 --duration 60
          dotnet run --project load\AntifraudBench -c Release -- --workers 1,2 --cpus 2 --redis-timeout-ms 500

          --workers 1,2,...  uvicorn processes in the container, one series each (default: 1,2,4)
          --rates 300,...    checks a second, one step each (default: 300,600,1000,1500)
          --repeats N        how many times each step is repeated (default: 2)
          --duration S       seconds of load in a step (default: 30)
          --warmup S         seconds of unmeasured load after each start of the container, at the first rate (default: 15; 0: none)
          --redis-timeout-ms N  ANTIFRAUD_REDIS_TIMEOUT_MS of the service (default: 20, the value of the cluster)
          --cpus C           a processor limit of the antifraud container, as a pod's limit (default: 0, none)
          --k6-image IMAGE   the k6 image (default: grafana/k6 of the version of the k6 installed on Windows)
          --no-build         use the image highload-antifraud:bench built before instead of building it again
        """;

    public static BenchOptions Parse(string[] args)
    {
        var options = new BenchOptions(
            Workers: [1, 2, 4],
            Rates: [300, 600, 1000, 1500],
            Repeats: 2,
            DurationSeconds: 30,
            WarmupSeconds: 15,
            RedisTimeoutMs: 20,
            Cpus: 0,
            K6Image: null,
            Build: true);

        for (var i = 0; i < args.Length; i++)
        {
            var name = args[i];
            string Value() => ++i < args.Length ? args[i] : throw Error($"{name} needs a value");

            options = name switch
            {
                "--workers" => options with { Workers = ParseList(name, Value(), min: 1) },
                "--rates" => options with { Rates = ParseList(name, Value(), min: 1) },
                "--repeats" => options with { Repeats = ParseNumber(name, Value(), min: 1) },
                "--duration" => options with { DurationSeconds = ParseNumber(name, Value(), min: 10) },
                "--warmup" => options with { WarmupSeconds = ParseNumber(name, Value(), min: 0) },
                "--redis-timeout-ms" => options with { RedisTimeoutMs = ParseNumber(name, Value(), min: 1) },
                "--cpus" => options with { Cpus = ParseCpus(Value()) },
                "--k6-image" => options with { K6Image = Value() },
                "--no-build" => options with { Build = false },
                _ => throw Error($"Unknown option {name}"),
            };
        }

        return options;
    }

    private static List<int> ParseList(string name, string value, int min)
    {
        var numbers = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(item => ParseNumber(name, item, min))
            .ToList();
        return numbers.Count > 0 ? numbers : throw Error($"{name} names nothing");
    }

    private static int ParseNumber(string name, string value, int min) =>
        int.TryParse(value, out var number) && number >= min
            ? number
            : throw Error($"{name} takes a whole number from {min}, got {value}");

    private static double ParseCpus(string value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var cpus) && cpus >= 0
            ? cpus
            : throw Error($"--cpus takes a number of processors from 0, got {value}");

    private static BenchException Error(string message) => new($"{message}\n\n{Usage}");
}
