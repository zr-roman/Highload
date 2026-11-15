using System.Globalization;
using Failover;

// A node of the k3d cluster fails under load, in one command: puts on the node what the scenario needs, stops it,
// samples the stand every 2 s, checks that nothing was lost and writes load/reports/failover-<time>/report.md.
// From the repository root, with the cluster up and no other load:
//   dotnet run --project load\Failover -c Release
// Options: FailoverOptions.Usage.

if (args is ["-h"] or ["--help"])
{
    Console.WriteLine(FailoverOptions.Usage);
    return 0;
}

CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

// The first Ctrl+C stops the run at the next await; a node already stopped is started again on the way out
// (Outage). A second one ends the process at once.
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = !cts.IsCancellationRequested;
    cts.Cancel();
};

try
{
    await Runner.RunAsync(FailoverOptions.Parse(args), cts.Token);
    return 0;
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("Stopped.");
    return 130;
}
catch (FailoverException e)
{
    Console.Error.WriteLine(e.Message);
    return 1;
}
