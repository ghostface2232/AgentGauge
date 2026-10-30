using Windows.Management.Deployment;

namespace Gauge.Services;

public interface ICliLocator
{
    string? Find(string commandName);
}

/// <summary>
/// Finds an official CLI: PATH and the usual per-user install folders first, then the copy
/// a desktop app ships. Claude's desktop app keeps its own token encrypted (Electron
/// safeStorage), which Gauge deliberately does not decode, but it bundles the real
/// <c>claude.exe</c>, and the Codex desktop app bundles <c>codex.exe</c> and shares
/// <c>~/.codex/auth.json</c> with the CLI. Running those bundled binaries for sign-in and
/// the delegated refresh lets a desktop-only install work while the CLI keeps sole
/// ownership of its credential file. A standalone CLI wins because it is the one the
/// user updates and runs themself.
/// </summary>
public sealed class CliLocator : ICliLocator
{
    // The Codex desktop app is an MSIX package; its install folder under
    // %ProgramFiles%\WindowsApps cannot be listed, only resolved through the package API.
    private const string CodexPackageFamily = "OpenAI.Codex_2p2nqsd0c76g0";

    public string? Find(string commandName) =>
        FindOnPath(commandName) ?? FindBundled(commandName);

    private static string? FindOnPath(string commandName)
    {
        var extensions = new[] { ".exe", ".cmd", ".bat", "" };
        var candidates = new List<string>();
        var path = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrWhiteSpace(path))
        {
            candidates.AddRange(path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }

        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        candidates.AddRange(new[]
        {
            appData is { Length: > 0 } ? Path.Combine(appData, "npm") : "",
            localAppData is { Length: > 0 } ? Path.Combine(localAppData, "Programs", commandName) : "",
            Path.Combine(profile, ".local", "bin"),
            Path.Combine(profile, ".claude", "local"),
        });

        foreach (var directory in candidates.Where(d => !string.IsNullOrWhiteSpace(d)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            foreach (var extension in extensions)
            {
                var candidate = Path.Combine(directory, commandName + extension);
                if (File.Exists(candidate)) return candidate;
            }
        }
        return null;
    }

    private static string? FindBundled(string commandName)
    {
        try
        {
            return commandName.ToLowerInvariant() switch
            {
                "claude" => FindClaudeDesktopCli(),
                "codex" => FindCodexDesktopCli(),
                _ => null,
            };
        }
        catch (Exception ex)
        {
            DiagnosticsLog.Write("auth", $"Desktop app CLI lookup failed: {ex.GetType().Name}");
            return null;
        }
    }

    private static string? FindClaudeDesktopCli()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roots = new List<string>();
        if (appData is { Length: > 0 }) roots.Add(Path.Combine(appData, "Claude", "claude-code"));
        // An MSIX install of the desktop app has its AppData redirected into its package.
        var packages = localAppData is { Length: > 0 } ? Path.Combine(localAppData, "Packages") : "";
        if (Directory.Exists(packages))
        {
            roots.AddRange(Directory.EnumerateDirectories(packages, "Claude_*")
                .Select(p => Path.Combine(p, "LocalCache", "Roaming", "Claude", "claude-code")));
        }
        return roots.Select(FindNewestClaudeBinary).FirstOrDefault(p => p is not null);
    }

    /// <summary>
    /// The desktop app downloads its CLI on first use of its Code tab to
    /// <c>claude-code\&lt;version&gt;\&lt;build hash&gt;\claude.exe</c> and can keep an older version
    /// beside the current one, so the highest version wins. It writes a <c>.verified</c> marker
    /// after the binary, so a build still downloading is passed over for an older complete one;
    /// an unmarked binary is used only when no build carries the marker.
    /// </summary>
    internal static string? FindNewestClaudeBinary(string root)
    {
        if (!Directory.Exists(root)) return null;
        var versions = Directory.EnumerateDirectories(root)
            .Select(dir => (dir, version: Version.TryParse(Path.GetFileName(dir), out var v) ? v : null))
            .Where(x => x.version is not null)
            .OrderByDescending(x => x.version);
        string? unverified = null;
        foreach (var (dir, _) in versions)
        {
            var builds = new[] { dir }.Concat(Directory.EnumerateDirectories(dir).Order(StringComparer.Ordinal));
            foreach (var build in builds)
            {
                var binary = Path.Combine(build, "claude.exe");
                if (!File.Exists(binary)) continue;
                if (File.Exists(Path.Combine(build, ".verified"))) return binary;
                unverified ??= binary;
            }
        }
        return unverified;
    }

    private static string? FindCodexDesktopCli()
    {
        foreach (var package in new PackageManager().FindPackagesForUser(string.Empty, CodexPackageFamily))
        {
            var candidate = CodexBinaryIn(package.InstalledLocation.Path);
            if (candidate is not null) return candidate;
        }
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return localAppData is { Length: > 0 }
            ? CodexBinaryIn(Path.Combine(localAppData, "Programs", "Codex"))
            : null;
    }

    internal static string? CodexBinaryIn(string installRoot)
    {
        var candidate = Path.Combine(installRoot, "app", "resources", "codex.exe");
        if (File.Exists(candidate)) return candidate;
        candidate = Path.Combine(installRoot, "resources", "codex.exe");
        return File.Exists(candidate) ? candidate : null;
    }
}
