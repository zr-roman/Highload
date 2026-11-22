using System.Globalization;
using SagaDrill;

// The saga on the k3d stand when the antifraud says no or goes away (stage 4), in one command. For now the
// preflight: the stand is ready for the drill, Prometheus has loaded the rules of highload.saga, and under a warm-up
// load the antifraud answers and every query of the saga row of the dashboard and of the saga's alerts finds its
// metrics. The promtool tests cannot check that: their series are named by hand. Writes
// load/reports/saga-drill-<time>/report.md.
// From the repository root, with the cluster up and no other load:
//   dotnet run --project load\SagaDrill -c Release
// Options: DrillOptions.Usage.

if (args is ["-h"] or ["--help"])
{
    Console.WriteLine(DrillOptions.Usage);
    return 0;
}

CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

// The first Ctrl+C stops the run at the next await; a second one ends the process at once.
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = !cts.IsCancellationRequested;
    cts.Cancel();
};

try
{
    await Runner.RunAsync(DrillOptions.Parse(args), cts.Token);
    return 0;
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("Stopped.");
    return 130;
}
catch (DrillException e)
{
    Console.Error.WriteLine(e.Message);
    return 1;
}
