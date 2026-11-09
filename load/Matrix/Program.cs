using System.Globalization;
using Matrix;

// The stage 3 load matrix in one command: starts the stand, runs every load of MatrixRun.All, measures the backlog
// that k6 cannot see and writes load/reports/matrix-<time>/table.md for docs/load-testing.md.
// From the repository root, with docker compose up -d and Postgres running:
//   dotnet run --project load\Matrix -c Release
// Options: MatrixOptions.Usage.

if (args is ["-h"] or ["--help"])
{
    Console.WriteLine(MatrixOptions.Usage);
    return 0;
}

// k6 arguments and the numbers the tool reads use a dot, whatever the Windows locale is.
// The table formats its own numbers the Russian way (Report).
CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

// The first Ctrl+C stops the matrix at the next await: the stand is stopped on the way out, and the table keeps
// the repeats done so far. A second one ends the process at once.
using var cts = new CancellationTokenSource();

Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = !cts.IsCancellationRequested;
    cts.Cancel();
};

try
{
    await MatrixRunner.RunAsync(MatrixOptions.Parse(args), cts.Token);
    return 0;
}
catch (Exception e) when (e is OperationCanceledException || (e is MatrixException && cts.IsCancellationRequested))
{
    // Ctrl+C reaches k6 and the services as well, and they may exit before the matrix sees the cancellation:
    // k6 stopped from outside ends the matrix the same way (K6.RunAsync), and "Payments.Api has exited"
    // is then only the echo of the Ctrl+C.
    Console.Error.WriteLine("Stopped.");
    return 130;
}
catch (MatrixException e)
{
    Console.Error.WriteLine(e.Message);
    return 1;
}
