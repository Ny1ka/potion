using System.Diagnostics;
using System.Text;

namespace Potion.Core.Engines;

/// <summary>Output of a finished child process.</summary>
/// <param name="Stderr">The last part of stderr (capped), for error mapping and logs.</param>
public sealed record ProcessResult(int ExitCode, string Stdout, string Stderr);

/// <summary>
/// Runs a program with an argument list (never through a shell), streams stdout line by line, and kills the
/// process (and anything it started) when the token is cancelled.
/// </summary>
public static class ProcessRunner
{
    public const int StderrLimit = 64 * 1024;

    public static async Task<ProcessResult> RunAsync(string executable, IEnumerable<string> arguments,
                                                     Action<string>? onStdoutLine = null, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = info };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var stderrLock = new object();
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            lock (stdout) stdout.Append(e.Data).Append('\n');
            onStdoutLine?.Invoke(e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            lock (stderrLock)
            {
                stderr.Append(e.Data).Append('\n');
                if (stderr.Length > StderrLimit) stderr.Remove(0, stderr.Length - StderrLimit);
            }
        };

        try { process.Start(); }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            throw ConversionException.ToolsMissing();
        }
        process.StandardInput.Close();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await process.WaitForExitAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw;
        }
        process.WaitForExit();   // flushes the asynchronous output readers

        lock (stderrLock) lock (stdout)
            return new ProcessResult(process.ExitCode, stdout.ToString(), stderr.ToString());
    }
}
