namespace Matrix;

/// <summary>The command line of the matrix.</summary>
public sealed record MatrixOptions(IReadOnlyList<int> Runs, int Repeats, int DurationSeconds, int WarmupSeconds, bool Fresh)
{
    public const string Usage = """
        Stage 3 load matrix: starts the bank emulator and both services, runs the loads one by one
        and writes load/reports/matrix-<time>/table.md.

        From the repository root, with docker compose up -d and Postgres running:
          dotnet run --project load\Matrix -c Release
          dotnet run --project load\Matrix -c Release -- --runs 3,6 --repeats 1 --duration 60
          dotnet run --project load\Matrix -c Release -- --fresh --repeats 3

          --runs 1,2,...  the runs of the matrix to do (default: all six)
          --repeats N     how many times each run is repeated (default: 2)
          --duration S    seconds of load in a run (default: 120)
          --warmup S      seconds of unmeasured load after each start of Payments.Api (default: 60; 0: none)
          --fresh         empty the databases payments and ledger, delete the topic payments.captured and
                          the consumer group of the ledger first: every matrix then starts from the same stand
        """;

    public static MatrixOptions Parse(string[] args)
    {
        var options = new MatrixOptions(
            MatrixRun.All.Select(run => run.Id).ToList(), 
            Repeats: 2, 
            DurationSeconds: 120, 
            // 20 s were not enough: the repeats got faster one after another for half an hour.
            WarmupSeconds: 60,
            Fresh: false);

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
                _ => throw Error($"Unknown option {name}"),
            };
        }

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
