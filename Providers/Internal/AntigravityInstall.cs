using System.Diagnostics;

namespace Gauge.Providers.Internal;

/// <summary>
/// Locates the Antigravity installation. The IDE installs per-user under
/// <c>%LOCALAPPDATA%\Programs\Antigravity</c>; its language server lives at
/// <c>resources\bin\language_server.exe</c> beneath that root. The root is used to launch the
/// engine directly in delegate mode.
/// </summary>
internal static class AntigravityInstall
{
    /// <summary>The install root if present on disk, else null (not installed).</summary>
    public static string? DefaultRoot()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrEmpty(localAppData))
        {
            return null;
        }

        var root = Path.Combine(localAppData, "Programs", "Antigravity");
        return Directory.Exists(root) ? root : null;
    }

    /// <summary>The standalone language server Gauge launches in delegate mode.</summary>
    public static string EngineExecutablePath(string installRoot)
        => Path.Combine(installRoot, "resources", "bin", "language_server.exe");

    /// <summary>
    /// The IDE version to pass as <c>--override_ide_version</c>, read from Antigravity.exe so it
    /// tracks updates instead of being hard-coded. Null if it cannot be read (the override is
    /// then simply omitted). Note this is the IDE version (e.g. 2.1.4), not the base VS Code
    /// version in product.json.
    /// </summary>
    public static string? ResolveIdeVersion(string installRoot)
    {
        try
        {
            var exe = Path.Combine(installRoot, "Antigravity.exe");
            return File.Exists(exe)
                ? NormalizeIdeVersion(FileVersionInfo.GetVersionInfo(exe).ProductVersion)
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // FileVersionInfo reports a 4-part version (e.g. "2.1.4.0"); the IDE passes the 3-part form
    // ("2.1.4"). Drop a trailing ".0" fourth segment, otherwise pass the value through.
    internal static string? NormalizeIdeVersion(string? productVersion)
    {
        if (string.IsNullOrWhiteSpace(productVersion))
        {
            return null;
        }

        var trimmed = productVersion.Trim();
        var parts = trimmed.Split('.');
        return parts.Length == 4 && parts[3] == "0" ? string.Join('.', parts[..3]) : trimmed;
    }
}
