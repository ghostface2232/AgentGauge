namespace Gauge.Services;

/// <summary>
/// Remembers which release the automatic install last launched. The app exits before Setup
/// can fail, so a Setup that keeps failing would otherwise end the app on every logon with
/// no window left to turn the switch off from. A version already tried and still on offer
/// did not land: it stays available on the footer button and is not launched again by itself.
/// </summary>
public interface IAutoInstallAttempts
{
    bool WasAttempted(Version version);

    /// <summary>False means the attempt could not be recorded, so nothing may be launched.</summary>
    bool TryRecordAttempt(Version version);
}

/// <summary>
/// Persists whether the launch-time update check installs a newer release by itself, in
/// <c>%APPDATA%\Gauge\settings.json</c> via <see cref="AppSettingsFile"/>. An absent key reads
/// as on, so existing installs update without having to find the switch. An unreadable file
/// reads as OFF: installing is not undoable, and this document may be the one saying the user
/// turned it off. Saving leaves other keys untouched.
/// </summary>
public sealed class AutoUpdateSettingsStore : IAutoInstallAttempts
{
    private readonly Func<string> _directory;

    public AutoUpdateSettingsStore(Func<string>? directory = null)
        => _directory = directory ?? (() => AppSettingsFile.DefaultDirectory);

    public bool Load()
        => AppSettingsFile.TryLoad(_directory(), out var settings) && (settings.AutoUpdate ?? true);

    /// <summary>False means the choice did not reach disk; the caller keeps the previous state.</summary>
    public bool TrySave(bool enabled)
        => AppSettingsFile.TrySave(_directory(), dto => dto.AutoUpdate = enabled);

    // An unreadable document counts as attempted, for the same reason it reads as off.
    public bool WasAttempted(Version version)
        => !AppSettingsFile.TryLoad(_directory(), out var settings)
            || settings.AutoUpdateAttemptedVersion == version.ToString(3);

    public bool TryRecordAttempt(Version version)
        => AppSettingsFile.TrySave(_directory(), dto => dto.AutoUpdateAttemptedVersion = version.ToString(3));
}
