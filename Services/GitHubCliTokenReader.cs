using System.Diagnostics;

namespace Gauge.Services;

/// <summary>
/// Reads the GitHub OAuth token from the gh CLI by running <c>gh auth token</c>. gh owns the
/// token's storage (keyring or hosts.yml) and its refresh, so reading it this way works
/// wherever gh is signed in and can never disturb gh's own login. The token is a secret: it is
/// returned in memory only and never logged. Portable — gh is found via PATH, not a fixed path.
/// </summary>
public interface IGitHubCliTokenReader
{
    /// <summary>
    /// Returns the gh OAuth token for github.com, or null when gh is not installed, is signed
    /// out, or the command fails/times out.
    /// </summary>
    Task<string?> ReadTokenAsync(CancellationToken cancellationToken);
}

public sealed class GitHubCliTokenReader : IGitHubCliTokenReader
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private readonly ICliLocator _locator;

    public GitHubCliTokenReader(ICliLocator? locator = null) => _locator = locator ?? new CliLocator();

    public async Task<string?> ReadTokenAsync(CancellationToken cancellationToken)
    {
        var executable = _locator.Find("gh");
        if (executable is null)
        {
            return null;
        }

        var startInfo = new ProcessStartInfo
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };

        // `--hostname github.com` pins the account even when gh has several. A .cmd/.bat shim
        // can't be started directly once UseShellExecute is false, so route those through
        // cmd.exe (the /s + outer-quote form keeps a quoted path with spaces intact).
        const string args = "auth token --hostname github.com";
        var extension = Path.GetExtension(executable);
        if (extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".bat", StringComparison.OrdinalIgnoreCase))
        {
            startInfo.FileName = "cmd.exe";
            startInfo.Arguments = $"/d /s /c \"\"{executable}\" {args}\"";
        }
        else
        {
            startInfo.FileName = executable;
            startInfo.Arguments = args;
        }

        try
        {
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return null;
            }

            using var timeoutCts = new CancellationTokenSource(Timeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
            // Read stdout (the token) and drain stderr so a full pipe can't deadlock WaitForExit.
            var stdoutTask = process.StandardOutput.ReadToEndAsync(linked.Token);
            var drainErr = process.StandardError.ReadToEndAsync(linked.Token);
            try
            {
                await process.WaitForExitAsync(linked.Token);
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                TryKill(process);
                return null;
            }

            var token = (await stdoutTask).Trim();
            try { await drainErr; } catch { /* stderr discarded */ }
            // Only a clean exit yields a usable token; a non-zero exit means gh is signed out.
            return process.ExitCode == 0 && token.Length > 0 ? token : null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
            // Never include the token or gh output in diagnostics; the type name is enough.
            Debug.WriteLine($"[Gauge] gh auth token failed: {ex.GetType().Name}");
            return null;
        }
    }

    private static void TryKill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch { }
    }
}
