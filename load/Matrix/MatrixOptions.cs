namespace Matrix;

/// <summary>The command line of the matrix.</summary>
public sealed record MatrixOptions(IReadOnlyList<int> Runs, int Repeats, int DurationSeconds, int WarmupSeconds)
{
    public const string Usage = """
        Stage 3 load matrix: starts the bank emulator and both services, runs the loads one by one
        and writes load/reports/matrix-<time>/table.md.

        From the repository root, with docker compose up -d and Postgres running:
          dotnet run --project load\Matrix -c Release
          dotnet run --project load\Matrix -c Release -- --runs 3,6 --repeats 1 --duration 60

          --runs 1,2,...  the runs of the matrix to do (default: all six)
          --repeats N     how many times each run is repeated (default: 2)
          --duration S    seconds of load in a run (default: 120)
          --warmup S      seconds of unmeasured load after each start of Payments.Api (default: 20; 0: none)
        """;

    public static MatrixOptions Parse(string[] args)
    {
        var options = new MatrixOptions(
            MatrixRun.All.Select(run => run.Id).ToList(), 
            Repeats: 2, 
            DurationSeconds: 120, 
            WarmupSeconds: 20);

        for (var i = 0; i < args.Length; i += 2)
        {
            var name = args[i];
            var value = i + 1 < args.Length ? args[i + 1] : throw Error($"{name} needs a value");

            options = name switch
            {
                "--runs" => options with { Runs = ParseRuns(value) },
                "--repeats" => options with { Repeats = ParseNumber(name, value, min: 1) },
                "--duration" => options with { DurationSeconds = ParseNumber(name, value, min: 10) },
                "--warmup" => options with { WarmupSeconds = ParseNumber(name, value, min: 0) },
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
