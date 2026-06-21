using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;

namespace Gauge.Providers.Internal;

/// <summary>
/// A running Antigravity language server Gauge can attach to: its PID, executable, the CSRF
/// token to authenticate with, and the loopback ports it is listening on. The token is a
/// secret — it is never written to logs (see <see cref="ToString"/>).
/// </summary>
internal sealed record AntigravityLanguageServer
{
    public required int ProcessId { get; init; }
    public required string ExecutablePath { get; init; }
    public required string CsrfToken { get; init; }
    public required IReadOnlyList<int> ListeningPorts { get; init; }

    public override string ToString()
        => $"AntigravityLanguageServer(pid={ProcessId}, ports=[{string.Join(',', ListeningPorts)}])";
}

/// <summary>
/// Finds the running Antigravity language server processes by querying WMI for
/// <c>language_server*</c> processes, reading each one's full command line, and keeping those
/// that pass <see cref="AntigravityProcessMatch"/>. The CSRF token comes from the command line;
/// the listening ports come from the OS TCP table for that PID.
///
/// Discovery never throws for ordinary conditions: a process that exits mid-scan, an
/// access-denied command line, or no Antigravity at all all yield no candidate rather than an
/// error. The caller (attach mode) treats an empty result as "fall back to delegate".
/// </summary>
internal sealed class AntigravityProcessDiscovery
{
    private readonly string? _installRoot;
    private readonly Func<int, IReadOnlyList<int>> _listeningPorts;

    public AntigravityProcessDiscovery(
        string? installRoot = null,
        Func<int, IReadOnlyList<int>>? listeningPorts = null)
    {
        _installRoot = installRoot ?? AntigravityInstall.DefaultRoot();
        _listeningPorts = listeningPorts ?? WindowsListeningPortTable.LoopbackListeningPorts;
    }

    public IReadOnlyList<AntigravityLanguageServer> Discover()
    {
        var servers = new List<AntigravityLanguageServer>();
        try
        {
            // "_" is a single-char wildcard in WQL LIKE; "[_]" matches a literal underscore.
            using var searcher = new ManagementObjectSearcher(
                "SELECT ProcessId, ExecutablePath, CommandLine FROM Win32_Process WHERE Name LIKE 'language[_]server%'");
            using var results = searcher.Get();

            foreach (ManagementBaseObject result in results)
            {
                using (result)
                {
                    if (TryBuild(result) is { } server)
                    {
                        servers.Add(server);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException or COMException)
        {
            Debug.WriteLine($"[Gauge] Antigravity process discovery failed: {ex.GetType().Name}");
        }

        return servers;
    }

    private AntigravityLanguageServer? TryBuild(ManagementBaseObject process)
    {
        if (process["ExecutablePath"] as string is not { Length: > 0 } executablePath
            || process["CommandLine"] as string is not { Length: > 0 } rawCommandLine)
        {
            return null;
        }

        var commandLine = new AntigravityCommandLine(rawCommandLine);
        if (!AntigravityProcessMatch.IsCandidate(executablePath, _installRoot, commandLine)
            || commandLine.GetValue("--csrf_token") is not { Length: > 0 } token)
        {
            return null;
        }

        var processId = Convert.ToInt32(process["ProcessId"]);
        IReadOnlyList<int> ports;
        try
        {
            ports = _listeningPorts(processId);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // The process may have exited between the WMI query and the port lookup.
            Debug.WriteLine($"[Gauge] Antigravity port lookup failed: {ex.GetType().Name}");
            ports = Array.Empty<int>();
        }

        return new AntigravityLanguageServer
        {
            ProcessId = processId,
            ExecutablePath = executablePath,
            CsrfToken = token,
            ListeningPorts = ports,
        };
    }
}
