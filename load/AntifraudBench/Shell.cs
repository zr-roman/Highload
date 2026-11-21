using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace AntifraudBench;

/// <summary>What a command wrote, and how it exited.</summary>
public sealed record CommandResult(int ExitCode, string Output, string Errors)
{
    /// <summary>The first line of what it wrote to stderr, or to stdout if stderr is empty: for an error message.</summary>
    public string FirstError => (Errors.Length > 0 ? Errors : Output).Split('\n')[0].Trim();
}

public static class Shell
{
    /// <summary>
    /// A command run to the end, with what it wrote. Cancelled: the process is killed. Not installed: BenchException.
    /// Both streams are read at once: a command that fills the pipe of an unread one would never exit.
    /// </summary>
    public static async Task<CommandResult> RunAsync(
        string file, IEnumerable<string> arguments, CancellationToken ct, string? workingDirectory = null)
    {
        using var process = new Process { StartInfo = Info(file, arguments, workingDirectory) };
        try
        {
            process.Start();
        }
        catch (Win32Exception e)
        {
            throw new BenchException($"Cannot start {file} ({e.Message}): is it installed and on PATH?");
        }

        var output = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var errors = process.StandardError.ReadToEndAsync(CancellationToken.None);
        try
        {
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            throw;
        }

        return new CommandResult(process.ExitCode, (await output).Trim(), (await errors).Trim());
    }

    /// <summary>The same, and a non-zero exit is a BenchException with the first line of the error.</summary>
    public static async Task<string> RunCheckedAsync(
        string what, string file, IEnumerable<string> arguments, CancellationToken ct, string? workingDirectory = null)
    {
        var result = await RunAsync(file, arguments, ct, workingDirectory);
        return result.ExitCode == 0
            ? result.Output
            : throw new BenchException($"{what} failed (exit code {result.ExitCode}): {result.FirstError}");
    }

    public static ProcessStartInfo Info(string file, IEnumerable<string> arguments, string? workingDirectory = null)
    {
        var info = new ProcessStartInfo(file)
        {
            WorkingDirectory = workingDirectory ?? "",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in arguments)
            info.ArgumentList.Add(argument);
        return info;
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
