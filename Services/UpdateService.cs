using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;

namespace Gauge.Services;

public enum UpdateStatus
{
    UpToDate,
    UpdateAvailable,
    CheckFailed,
}

/// <summary>A release found on GitHub, with the installer asset to download.</summary>
public sealed record GitHubRelease(Version Version, string TagName, string DownloadUrl);

public sealed record UpdateCheckResult(UpdateStatus Status, Version CurrentVersion, GitHubRelease? Release);

/// <summary>
/// Checks GitHub Releases for a newer build and applies it by downloading the
/// per-user installer and launching it silently. The installer closes the running
/// app, updates in place, and relaunches Gauge (the WizardSilent [Run] entry).
/// </summary>
public sealed class UpdateService
{
    private const string Owner = "ghostface2232";
    // External brand/repo is AgentGauge; the app's internal identity stays "Gauge".
    // GitHub redirects the old "gauge" path here, so updaters from older builds still
    // resolve releases through the rename.
    private const string Repo = "AgentGauge";
    private const string AssetName = "GaugeSetup-win-x64.exe";

    private readonly HttpClient _http;
    private readonly Version _currentVersion;

    public UpdateService()
    {
        // Dedicated client: GitHub requires a User-Agent, and a 60 MB installer
        // download needs a longer timeout than the usage HttpClient allows.
        _http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Gauge-Updater");
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        _currentVersion = Normalize(Assembly.GetExecutingAssembly().GetName().Version);
    }

    public Version CurrentVersion => _currentVersion;

    /// <summary>Queries the latest stable release; never throws (failures map to CheckFailed).</summary>
    public async Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var url = $"https://api.github.com/repos/{Owner}/{Repo}/releases/latest";
            using var doc = JsonDocument.Parse(await _http.GetStringAsync(url, cancellationToken));
            var root = doc.RootElement;

            var tag = root.GetProperty("tag_name").GetString() ?? "";
            if (!TryParseVersion(tag, out var version))
            {
                return new UpdateCheckResult(UpdateStatus.CheckFailed, _currentVersion, null);
            }

            string? downloadUrl = null;
            if (root.TryGetProperty("assets", out var assets))
            {
                foreach (var asset in assets.EnumerateArray())
                {
                    if (string.Equals(asset.GetProperty("name").GetString(), AssetName, StringComparison.OrdinalIgnoreCase))
                    {
                        downloadUrl = asset.GetProperty("browser_download_url").GetString();
                        break;
                    }
                }
            }

            if (string.IsNullOrEmpty(downloadUrl))
            {
                return new UpdateCheckResult(UpdateStatus.CheckFailed, _currentVersion, null);
            }

            var release = new GitHubRelease(version, tag, downloadUrl);
            var status = version > _currentVersion ? UpdateStatus.UpdateAvailable : UpdateStatus.UpToDate;
            return new UpdateCheckResult(status, _currentVersion, release);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Gauge] UpdateService.CheckAsync failed: {ex.Message}");
            return new UpdateCheckResult(UpdateStatus.CheckFailed, _currentVersion, null);
        }
    }

    /// <summary>
    /// Downloads the installer and starts it silently. Returns true once the
    /// installer process has launched; the caller should then exit the app so
    /// the installer can replace the locked files and relaunch Gauge.
    /// </summary>
    public async Task<bool> DownloadAndLaunchAsync(GitHubRelease release, CancellationToken cancellationToken = default)
    {
        try
        {
            var dir = Path.Combine(Path.GetTempPath(), "Gauge");
            Directory.CreateDirectory(dir);
            var installer = Path.Combine(dir, $"GaugeSetup-{release.TagName}.exe");

            await using (var stream = await _http.GetStreamAsync(release.DownloadUrl, cancellationToken))
            await using (var file = File.Create(installer))
            {
                await stream.CopyToAsync(file, cancellationToken);
            }

            // /VERYSILENT hides the installer UI entirely (no progress window); the
            // in-app ring spinner stands in for it. CloseApplications=yes closes the
            // running app, and the WizardSilent [Run] entry relaunches it afterwards.
            Process.Start(new ProcessStartInfo
            {
                FileName = installer,
                Arguments = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART",
                UseShellExecute = false,
            });
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Gauge] UpdateService.DownloadAndLaunchAsync failed: {ex.Message}");
            return false;
        }
    }

    private static Version Normalize(Version? v) =>
        v is null ? new Version(0, 0, 0) : new Version(v.Major, Math.Max(v.Minor, 0), Math.Max(v.Build, 0));

    private static bool TryParseVersion(string tag, out Version version)
    {
        var trimmed = tag.TrimStart('v', 'V').Trim();
        // Drop any pre-release suffix (e.g. "0.2.0-beta.1").
        var dash = trimmed.IndexOf('-');
        if (dash >= 0) trimmed = trimmed[..dash];

        if (Version.TryParse(trimmed, out var parsed))
        {
            version = Normalize(parsed);
            return true;
        }

        version = new Version(0, 0, 0);
        return false;
    }
}
