using System.Diagnostics;

namespace Gauge.Services;

public sealed record CliProcessResult(int ExitCode, bool TimedOut);

public interface ICliProcessRunner
{
    Task<CliProcessResult> RunVisibleAsync(string executable, string arguments, TimeSpan timeout, CancellationToken cancellationToken);
}

public sealed class CliProcessRunner : ICliProcessRunner
{
    public async Task<CliProcessResult> RunVisibleAsync(
        string executable, string arguments, TimeSpan timeout, CancellationToken cancellationToken)
    {
        // Do not redirect stdout/stderr: login is intentionally user-visible and its
        // output may contain secrets that Gauge must never capture or log.
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = executable,
            Arguments = arguments,
            UseShellExecute = true,
            CreateNoWindow = false,
            WindowStyle = ProcessWindowStyle.Normal,
        }) ?? throw new InvalidOperationException("CLI 로그인 프로세스를 시작할 수 없습니다.");

        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
        try
        {
            await process.WaitForExitAsync(linked.Token);
            return new CliProcessResult(process.ExitCode, TimedOut: false);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            return new CliProcessResult(-1, TimedOut: true);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }
    }

    private static void TryKill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch { }
    }
}
