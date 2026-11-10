using System.Diagnostics;

namespace Gauge.Services;

public sealed record CliProcessResult(int ExitCode, bool TimedOut);

public interface ICliProcessRunner
{
    Task<CliProcessResult> RunVisibleAsync(string executable, string arguments, TimeSpan timeout, CancellationToken cancellationToken);

    /// <summary>
    /// Runs a CLI command with no window and no user interaction, draining and
    /// discarding its output. Used for background, fire-and-forget commands such as
    /// nudging the Claude CLI to refresh its own OAuth token. On timeout the process
    /// is killed and <see cref="CliProcessResult.TimedOut"/> is set.
    /// </summary>
    Task<CliProcessResult> RunHiddenAsync(string executable, string arguments, TimeSpan timeout, CancellationToken cancellationToken);
}

public sealed class CliProcessRunner(
    IReadOnlyDictionary<string, string?>? environment = null, string? configDirectory = null) : ICliProcessRunner
{
    /// <summary>
    /// The folder every CLI starts in: the user's profile rather than whatever folder Gauge
    /// inherited, so the CLI never picks up project files (or a planted binary) from there.
    /// </summary>
    internal static string NeutralWorkingDirectory { get; } = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>
    /// cmd.exe by full path: a bare name makes CreateProcess search the working directory
    /// before the system folder.
    /// </summary>
    internal static string CommandInterpreter { get; } = Path.Combine(Environment.SystemDirectory, "cmd.exe");

    public async Task<CliProcessResult> RunVisibleAsync(
        string executable, string arguments, TimeSpan timeout, CancellationToken cancellationToken)
    {
        // Do not redirect stdout/stderr: login is intentionally user-visible and its
        // output may contain secrets that Gauge must never capture or log.
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            Arguments = arguments,
            UseShellExecute = environment is null,
            CreateNoWindow = false,
            WindowStyle = ProcessWindowStyle.Normal,
            WorkingDirectory = NeutralWorkingDirectory,
        };
        if (environment is not null)
            ConfigureProcess(startInfo, executable, arguments);
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("CLI 로그인 프로세스를 시작할 수 없습니다.");

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

    public async Task<CliProcessResult> RunHiddenAsync(
        string executable, string arguments, TimeSpan timeout, CancellationToken cancellationToken)
    {
        // Hidden background run: no window, output redirected and discarded. The output
        // (e.g. `claude auth status`) may carry account info, so it is never logged.
        var startInfo = new ProcessStartInfo
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = NeutralWorkingDirectory,
        };

        ConfigureProcess(startInfo, executable, arguments);
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("CLI 프로세스를 시작할 수 없습니다.");

        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
        // Drain both streams so a full pipe buffer can never deadlock WaitForExit.
        var drainOut = process.StandardOutput.ReadToEndAsync(linked.Token);
        var drainErr = process.StandardError.ReadToEndAsync(linked.Token);
        try
        {
            await process.WaitForExitAsync(linked.Token);
            try { await Task.WhenAll(drainOut, drainErr); } catch { /* output discarded */ }
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

    private void ConfigureProcess(ProcessStartInfo startInfo, string executable, string arguments)
    {
        if (configDirectory is not null) Directory.CreateDirectory(configDirectory);
        if (environment is not null)
        {
            foreach (var (name, value) in environment)
            {
                if (value is null) startInfo.Environment.Remove(name);
                else startInfo.Environment[name] = value;
            }
        }

        // npm's .cmd/.bat shims need cmd.exe when UseShellExecute is false.
        var extension = Path.GetExtension(executable);
        if (extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".bat", StringComparison.OrdinalIgnoreCase))
        {
            startInfo.FileName = CommandInterpreter;
            startInfo.Arguments = $"/d /s /c \"\"{executable}\" {arguments}\"";
        }
        else
        {
            startInfo.FileName = executable;
            startInfo.Arguments = arguments;
        }
    }

    private static void TryKill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch { }
    }
}
