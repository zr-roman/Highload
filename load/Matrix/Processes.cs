using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace Matrix;

public static class Processes
{
    /// <summary>
    /// A command with its output redirected. It gets the environment of the matrix without the stand's settings
    /// (<see cref="IsStandSetting"/>), plus the settings given here.
    /// </summary>
    public static ProcessStartInfo Info(
        string file, IEnumerable<string> arguments, string? workingDirectory = null, IReadOnlyDictionary<string, string>? settings = null)
    {
        var info = new ProcessStartInfo(file)
        {
            WorkingDirectory = workingDirectory ?? "",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            // k6 and the emulator write UTF-8.
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in arguments)
            info.ArgumentList.Add(argument);

        foreach (var name in info.Environment.Keys.Where(IsStandSetting).ToList())
            info.Environment.Remove(name);

        foreach (var (name, value) in settings ?? new Dictionary<string, string>())
            info.Environment[name] = value;

        return info;
    }

    /// <summary>
    /// Variables that configure the stand: .NET settings (Section__Key), the bank emulator (BANK_*), OpenTelemetry (OTEL_*)
    /// and k6 (K6_*). One left in the shell from a manual experiment, such as a fake bank, a slow bank or a disabled limiter,
    /// would change the numbers and show up nowhere. So no process of the matrix gets them: the stand is what
    /// appsettings.json, the launch profiles and the matrix itself say.
    /// </summary>
    public static bool IsStandSetting(string name) =>
        name.IndexOf("__", StringComparison.Ordinal) > 0 // Windows has variables of its own that start with __
        || name.StartsWith("BANK_", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("OTEL_", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("K6_", StringComparison.OrdinalIgnoreCase);

    /// <summary>Runs a command to the end with its output in a log file; returns the exit code.</summary>
    public static async Task<int> RunAsync(ProcessStartInfo info, string logPath, CancellationToken ct)
    {
        using var process = new LoggedProcess(info.FileName, info, logPath);
        await process.WaitForExitAsync(ct); // cancelled: Dispose kills it
        return process.ExitCode;
    }

    /// <summary>The output of a short command, or null if it is not installed or fails.</summary>
    public static async Task<string?> ReadAsync(string file, params string[] arguments) =>
        await RunCommandAsync(file, arguments) is { ExitCode: 0 } result ? result.Output : null;

    /// <summary>A short command run to the end, with what it wrote; null if it is not installed.</summary>
    public static async Task<CommandResult?> RunCommandAsync(string file, params string[] arguments)
    {
        try
        {
            using var process = Process.Start(Info(file, arguments))!;
            // Both streams are read: a command that fills the pipe of an unread one would never exit. On Windows each
            // read holds a thread of the pool (LoggedProcess), which for a short command before the stand starts is not missed.
            var output = process.StandardOutput.ReadToEndAsync();
            var errors = process.StandardError.ReadToEndAsync();
            await Task.WhenAll(output, errors, process.WaitForExitAsync());
            return new CommandResult(process.ExitCode, output.Result.Trim(), errors.Result.Trim());
        }
        catch (Win32Exception)
        {
            return null;
        }
    }
}

/// <summary>What a short command wrote, and how it exited.</summary>
public sealed record CommandResult(int ExitCode, string Output, string Errors);

/// <summary>A process whose output, both streams, goes line by line to a log file. Dispose kills it with its children.</summary>
public sealed class LoggedProcess : IDisposable
{
    private readonly Process process;
    private readonly StreamWriter log;
    private readonly Action<string>? onLine;
    private readonly Thread[] readers;
    private bool closed;

    public LoggedProcess(string name, ProcessStartInfo info, string logPath, Action<string>? onLine = null)
    {
        Name = name;
        LogPath = logPath;
        this.onLine = onLine;
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
            throw new MatrixException($"Cannot start {info.FileName} ({e.Message}): is it installed and on PATH?");
        }

        // Not BeginOutputReadLine: before .NET 11, on Windows it reads each redirected stream by blocking a thread of
        // the pool until the process writes. With k6 the stand has eight streams, and the pool starts with a thread per
        // logical processor, eight on a 4-core laptop: the timers and awaits of the matrix found no thread free, and its
        // samples of the backlog came up to 90 s late. A thread of its own per stream holds up nothing else.
        readers = [Read(process.StandardOutput), Read(process.StandardError)];
    }

    public string Name { get; }

    public string LogPath { get; }

    public bool HasExited => process.HasExited;

    public int ExitCode => process.ExitCode;

    public Task WaitForExitAsync(CancellationToken ct) => process.WaitForExitAsync(ct);

    public void Dispose()
    {
        try
        {
            // dotnet run and go run start the program as a child process: without the tree only the launcher would die.
            process.Kill(entireProcessTree: true);
        }
        catch (Exception e) when (e is InvalidOperationException or Win32Exception)
        {
            // It has exited already.
        }
        process.WaitForExit(TimeSpan.FromSeconds(10));

        // The last lines may still be in the pipes after the exit. A stream that a child left alive still holds open
        // does not end at all: that is what the timeout is for.
        foreach (var reader in readers)
            reader.Join(TimeSpan.FromSeconds(5));

        lock (log)
        {
            closed = true;
            log.Dispose();
        }
        process.Dispose();
    }

    private Thread Read(StreamReader stream)
    {
        var reader = new Thread(() =>
        {
            try
            {
                while (stream.ReadLine() is { } line)
                    Copy(line);
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException)
            {
                // Dispose has closed a stream that had not ended.
            }
        })
        {
            IsBackground = true, // a stream that does not end does not keep the matrix from exiting
        };
        reader.Start();
        return reader;
    }

    // Called on two threads, one per stream. After Dispose the lines have nowhere to go.
    private void Copy(string line)
    {
        lock (log)
        {
            if (closed)
                return;

            log.WriteLine(line);
        }
        onLine?.Invoke(line);
    }
}
