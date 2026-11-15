using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace Failover;

/// <summary>What a short command wrote, and how it exited.</summary>
public sealed record CommandResult(int ExitCode, string Output, string Errors)
{
    public bool Ok => ExitCode == 0;
}

/// <summary>The commands the tool drives the stand with: kubectl, docker, k6, git.</summary>
public static class Shell
{
    public static async Task<CommandResult> RunAsync(
        string file, IEnumerable<string> arguments, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        var info = new ProcessStartInfo(file)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in arguments)
            info.ArgumentList.Add(argument);

        Process process;
        try
        {
            process = Process.Start(info)!;
        }
        catch (Win32Exception e)
        {
            throw new FailoverException($"Cannot start {file} ({e.Message}): is it installed and on PATH?");
        }

        using (process)
        {
            // Both streams are read: a command that fills the pipe of an unread one would never exit.
            var output = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
            var errors = process.StandardError.ReadToEndAsync(CancellationToken.None);
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(timeout ?? TimeSpan.FromSeconds(30));
            try
            {
                await process.WaitForExitAsync(limit.Token);
            }
            catch (OperationCanceledException)
            {
                Kill(process);
                ct.ThrowIfCancellationRequested();
                // A kubectl exec into a pod on a dead node can hang: for a sample that is "no answer", not an error.
                return new CommandResult(-1, "", $"{file} did not finish in time");
            }
            return new CommandResult(process.ExitCode, (await output).Trim(), (await errors).Trim());
        }
    }

    public static Task<CommandResult> Kubectl(params string[] arguments) => RunAsync("kubectl", arguments);

    public static Task<CommandResult> Kubectl(TimeSpan timeout, params string[] arguments) =>
        RunAsync("kubectl", arguments, timeout);

    /// <summary>kubectl that must succeed: the stand is not as the tool expects otherwise.</summary>
    public static async Task<string> KubectlOrThrow(params string[] arguments)
    {
        var result = await RunAsync("kubectl", arguments, TimeSpan.FromSeconds(60));
        if (!result.Ok)
            throw new FailoverException($"kubectl {string.Join(' ', arguments)} failed: {result.Errors}");
        return result.Output;
    }

    public static void Kill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception e) when (e is InvalidOperationException or Win32Exception)
        {
            // It has exited already.
        }
    }
}

/// <summary>
/// A long command (k6, kubectl port-forward) with its output in a log file. Each stream is read by a thread of its
/// own, not by the thread pool, which the samplers need on time (load/Matrix/Processes.cs explains why).
/// Dispose kills it.
/// </summary>
public sealed class LongProcess : IDisposable
{
    private readonly Process process;
    private readonly StreamWriter log;
    private readonly Thread[] readers;

    public LongProcess(string file, IEnumerable<string> arguments, string logPath, IReadOnlyDictionary<string, string>? environment = null)
    {
        var info = new ProcessStartInfo(file)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in arguments)
            info.ArgumentList.Add(argument);
        foreach (var (name, value) in environment ?? new Dictionary<string, string>())
            info.Environment[name] = value;

        log = new StreamWriter(logPath) { AutoFlush = true };
        process = new Process { StartInfo = info };
        try
        {
            process.Start();
        }
        catch (Win32Exception e)
        {
            log.Dispose();
            process.Dispose();
            throw new FailoverException($"Cannot start {file} ({e.Message}): is it installed and on PATH?");
        }
        readers = [Read(process.StandardOutput), Read(process.StandardError)];
    }

    public int ExitCode => process.ExitCode;

    public Task WaitForExitAsync(CancellationToken ct) => process.WaitForExitAsync(ct);

    public void Dispose()
    {
        Shell.Kill(process);
        process.WaitForExit(TimeSpan.FromSeconds(10));
        foreach (var reader in readers)
            reader.Join(TimeSpan.FromSeconds(5));
        lock (log)
            log.Dispose();
        process.Dispose();
    }

    private Thread Read(StreamReader stream)
    {
        var reader = new Thread(() =>
        {
            try
            {
                while (stream.ReadLine() is { } line)
                    lock (log)
                        if (log.BaseStream.CanWrite)
                            log.WriteLine(line);
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException)
            {
                // Dispose has closed a stream that had not ended.
            }
        })
        {
            IsBackground = true,
        };
        reader.Start();
        return reader;
    }
}
