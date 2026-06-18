using System.Diagnostics;
using System.Text;

namespace Gauge.Services;

/// <summary>
/// Thrown when a subprocess exits with a non-zero code. Carries the exit code and
/// captured standard error so callers can decide how to isolate the failure.
/// </summary>
public sealed class ProcessRunException : Exception
{
    public ProcessRunException(string fileName, string arguments, int exitCode, string standardError)
        : base($"Process '{fileName} {arguments}' exited with code {exitCode}." +
               (string.IsNullOrWhiteSpace(standardError) ? string.Empty : $" stderr: {standardError.Trim()}"))
    {
        ExitCode = exitCode;
        StandardError = standardError;
    }

    public int ExitCode { get; }

    public string StandardError { get; }
}

/// <summary>
/// Runs an external process, captures its full standard output, and enforces a
/// timeout. Throws <see cref="TimeoutException"/> on timeout and
/// <see cref="ProcessRunException"/> on non-zero exit so the caller can isolate
/// each invocation in its own try/catch.
/// </summary>
public sealed class ProcessRunner
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

    public async Task<string> RunAsync(
        string fileName,
        string arguments,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        using var process = new Process { StartInfo = startInfo };
        process.Start();

        // Read both streams concurrently to avoid pipe-buffer deadlocks.
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout ?? DefaultTimeout);

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            throw new TimeoutException($"Process '{fileName} {arguments}' timed out after {(timeout ?? DefaultTimeout).TotalSeconds:0.#}s.");
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        if (process.ExitCode != 0)
        {
            throw new ProcessRunException(fileName, arguments, process.ExitCode, stderr);
        }

        return stdout;
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Best-effort cleanup; the process may have already exited.
        }
    }
}
