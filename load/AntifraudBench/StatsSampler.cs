using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace AntifraudBench;

/// <summary>
/// The processor of the containers while k6 runs, from docker stats, about once a second: what k6 cannot see — whether
/// the service is out of processor, or Redis, or k6 itself, which shares the cores of the VM of Docker with them.
/// 100 % is one core.
/// </summary>
public sealed partial class StatsSampler
{
    private readonly Dictionary<string, List<double>> samples = [];
    private readonly Lock gate = new();
    private Process? process;
    private Thread? reader;

    public void Start(params string[] containers)
    {
        lock (gate)
            samples.Clear();

        var info = Shell.Info("docker", ["stats", "--format", "{{.Name}};{{.CPUPerc}}", .. AllContainers(containers)]);
        process = Process.Start(info) ?? throw new BenchException("Cannot start docker stats");
        // A thread of its own, not the thread pool: it blocks on the pipe for the whole step.
        reader = new Thread(Read) { IsBackground = true };
        reader.Start(process.StandardOutput);
    }

    /// <summary>The antifraud container and Redis are always watched; k6 is added by its runner.</summary>
    private static IEnumerable<string> AllContainers(string[] extra) =>
        new[] { Stand.Container, Stand.RedisContainer }.Concat(extra).Distinct();

    public void Stop()
    {
        if (process is null)
            return;

        Shell.Kill(process);
        process.WaitForExit(5000);
        reader?.Join(TimeSpan.FromSeconds(5));
        process.Dispose();
        process = null;
    }

    /// <summary>Median and peak of the processor of a container over the step, in % of one core.</summary>
    public CpuUsage Of(string container)
    {
        lock (gate)
        {
            // The first sample of docker stats is taken before it has two readings to compare, often 0: dropped.
            var values = samples.TryGetValue(container, out var list) ? list.Skip(1).Order().ToList() : [];
            return values.Count == 0
                ? new CpuUsage(0, 0, 0)
                : new CpuUsage(values[values.Count / 2], values[^1], values.Count);
        }
    }

    private void Read(object? state)
    {
        var stream = (StreamReader)state!;
        try
        {
            while (stream.ReadLine() is { } line)
            {
                // Without a terminal docker stats still clears the screen before every frame: escape sequences first.
                var match = Sample().Match(Escape().Replace(line, ""));
                if (!match.Success)
                    continue;

                var cpu = double.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
                lock (gate)
                {
                    if (!samples.TryGetValue(match.Groups[1].Value, out var list))
                        samples[match.Groups[1].Value] = list = [];
                    list.Add(cpu);
                }
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException)
        {
            // Stop has killed it.
        }
    }

    [GeneratedRegex(@"^([\w.-]+);([\d.]+)%")]
    private static partial Regex Sample();

    [GeneratedRegex(@"\x1b\[[0-9;]*[A-Za-z]")]
    private static partial Regex Escape();
}

public readonly record struct CpuUsage(double Median, double Peak, int Samples);
