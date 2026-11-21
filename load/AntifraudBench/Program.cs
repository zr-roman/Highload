using System.Globalization;
using AntifraudBench;

// The antifraud service under load, in one command. The service, Redis and k6 all run in Linux containers on the
// network of docker compose, as the service runs in the cluster: no Windows clock, no port forwarding of Docker Desktop
// between them (docs/load-testing.md, «Этап 4: антифрод, первый замер»). For every number of uvicorn processes the bench
// starts a container, checks that all its processes are alive, warms it up and runs every rate, each repeat on an empty
// Redis, sampling the processor of every container. The result: load/reports/antifraud-bench-<time>/table.md.
// From the repository root, with Docker Desktop running:
//   dotnet run --project load\AntifraudBench -c Release
// Options: BenchOptions.Usage.

if (args is ["-h"] or ["--help"])
{
    Console.WriteLine(BenchOptions.Usage);
    return 0;
}

// k6 arguments and the numbers the bench reads use a dot, whatever the Windows locale is.
CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

// The first Ctrl+C stops the bench at the next await: the containers are removed on the way out, and the table keeps
// the runs done so far. A second one ends the process at once.
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = !cts.IsCancellationRequested;
    cts.Cancel();
};

try
{
    await BenchRunner.RunAsync(BenchOptions.Parse(args), cts.Token);
    return 0;
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("Stopped.");
    return 130;
}
catch (BenchException e)
{
    Console.Error.WriteLine(e.Message);
    return 1;
}
