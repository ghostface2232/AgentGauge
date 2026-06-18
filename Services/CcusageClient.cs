using System.IO;

namespace Gauge.Services;

/// <summary>
/// Invokes the globally installed ccusage CLI and returns its raw stdout.
///
/// On Windows ccusage is an npm shim (ccusage.cmd), which cannot be launched
/// directly with UseShellExecute=false, so we go through cmd.exe. We resolve the
/// shim from PATH (and the npm global folder as a fallback); if not found we still
/// pass the bare name and let cmd.exe resolve it from its own PATH.
/// </summary>
public sealed class CcusageClient
{
    private readonly ProcessRunner _runner;
    private readonly Lazy<string> _executable;

    public CcusageClient(ProcessRunner runner)
    {
        _runner = runner;
        _executable = new Lazy<string>(ResolveExecutable);
    }

    /// <summary>
    /// Runs <c>ccusage &lt;arguments&gt;</c> and returns stdout. Throws on timeout or
    /// non-zero exit (see <see cref="ProcessRunner"/>) so callers can isolate it.
    /// </summary>
    public Task<string> RunAsync(string arguments, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        var executable = _executable.Value;
        // cmd.exe /s /c "<command>": with /s, cmd keeps everything between the outer
        // quotes verbatim, which handles a quoted executable path plus arguments.
        var cmdArguments = $"/d /s /c \"\"{executable}\" {arguments}\"";
        return _runner.RunAsync("cmd.exe", cmdArguments, timeout ?? ProcessRunner.DefaultTimeout, cancellationToken);
    }

    private static string ResolveExecutable()
    {
        string[] names = ["ccusage.cmd", "ccusage.exe", "ccusage.bat"];

        var searchDirs = new List<string>();
        var path = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrEmpty(path))
        {
            searchDirs.AddRange(path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }

        // npm installs global shims here on Windows.
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (!string.IsNullOrEmpty(appData))
        {
            searchDirs.Add(Path.Combine(appData, "npm"));
        }

        foreach (var dir in searchDirs)
        {
            foreach (var name in names)
            {
                try
                {
                    var candidate = Path.Combine(dir, name);
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
                catch
                {
                    // Ignore malformed PATH entries.
                }
            }
        }

        // Fall back to the bare name; cmd.exe will resolve it from its PATH.
        return "ccusage";
    }
}
