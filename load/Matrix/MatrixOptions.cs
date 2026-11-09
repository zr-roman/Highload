namespace Matrix;

/// <summary>The command line of the matrix.</summary>
public sealed record MatrixOptions(
    IReadOnlyList<int> Runs, int Repeats, int DurationSeconds, int WarmupSeconds, bool Fresh, int Instances, int RedisPauseSeconds,
    int HotQuota)
{
    /// <summary>The burst of the hot merchant's quota: twice its rate, as the default limit has it (200 and 400).</summary>
    public int HotBurst => 2 * HotQuota;

    public const string Usage = """
        Stage 3 load matrix: starts the bank emulator and both services, runs the loads one by one
        and writes load/reports/matrix-<time>/table.md.

        From the repository root, with docker compose up -d and Postgres running:
          dotnet run --project load\Matrix -c Release
          dotnet run --project load\Matrix -c Release -- --runs 3,6 --repeats 1 --duration 60
          dotnet run --project load\Matrix -c Release -- --fresh --repeats 3
          dotnet run --project load\Matrix -c Release -- --runs 2,5 --repeats 3 --instances 2 --redis-pause 10
          dotnet run --project load\Matrix -c Release -- --fresh --runs 3,5 --repeats 3 --hot-quota 1000

          --runs 1,2,...  the runs of the matrix to do (default: all six)
          --repeats N     how many times each run is repeated (default: 2)
          --duration S    seconds of load in a run (default: 120)
          --warmup S      seconds of unmeasured load after each start of Payments.Api (default: 60; 0: none)
          --fresh         empty the databases payments and ledger, delete the topic payments.captured and
                          the consumer group of the ledger first: every matrix then starts from the same stand
          --instances N   instances of Payments.Api, on ports 5100, 5101, ...; k6 sends to them in turn (default: 1)
          --redis-pause S hold every command to Redis for S seconds in the middle of each repeat, as a Redis
                          that hangs (default: 0, no pause)
          --hot-quota R   a quota of its own for the hot merchant: R a second and a burst of 2R
                          (default: 0, the default limit of every merchant)
        """;

    public static MatrixOptions Parse(string[] args)
    {
        var options = new MatrixOptions(
            MatrixRun.All.Select(run => run.Id).ToList(), 
            Repeats: 2, 
            DurationSeconds: 120, 
            // 20 s were not enough: the repeats got faster one after another for half an hour.
            WarmupSeconds: 60,
            Fresh: false,
            Instances: 1,
            RedisPauseSeconds: 0,
            HotQuota: 0);

        for (var i = 0; i < args.Length; i++)
        {
            var name = args[i];
            string Value() => ++i < args.Length ? args[i] : throw Error($"{name} needs a value");

            options = name switch
            {
                "--fresh" => options with { Fresh = true },
                "--runs" => options with { Runs = ParseRuns(Value()) },
                "--repeats" => options with { Repeats = ParseNumber(name, Value(), min: 1) },
                "--duration" => options with { DurationSeconds = ParseNumber(name, Value(), min: 10) },
                "--warmup" => options with { WarmupSeconds = ParseNumber(name, Value(), min: 0) },
                "--instances" => options with { Instances = ParseNumber(name, Value(), min: 1) },
                "--redis-pause" => options with { RedisPauseSeconds = ParseNumber(name, Value(), min: 0) },
                "--hot-quota" => options with { HotQuota = ParseNumber(name, Value(), min: 0) },
                _ => throw Error($"Unknown option {name}"),
            };
        }

        // The pause sits in the middle of the load, with load before and after it.
        if (options.RedisPauseSeconds >= options.DurationSeconds)
            throw Error($"--redis-pause {options.RedisPauseSeconds} must be shorter than --duration {options.DurationSeconds}");

        return options;
    }

    private static List<int> ParseRuns(string value)
    {
        var runs = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(run => int.TryParse(run, out var id) && MatrixRun.All.Any(r => r.Id == id) ? id : throw Error($"No run {run} in the matrix"))
            .ToList();

        return runs.Count > 0 ? runs : throw Error("--runs names no run");
    }

    private static int ParseNumber(string name, string value, int min) =>
        int.TryParse(value, out var number) && number >= min
            ? number
            : throw Error($"{name} takes a whole number from {min}, got {value}");

    private static MatrixException Error(string message) => new($"{message}\n\n{Usage}");
}
