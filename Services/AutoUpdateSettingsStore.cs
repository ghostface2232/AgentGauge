namespace Gauge.Services;

/// <summary>
/// Persists whether the launch-time update check installs a newer release by itself, in
/// <c>%APPDATA%\Gauge\settings.json</c> via <see cref="AppSettingsFile"/>. The default is
/// on — an absent key reads as <c>true</c> — so existing installs update without having to
/// find the switch; an unreadable file reads the same way, which is why <c>App</c> keeps the
/// last persisted value in memory rather than re-reading to undo a failed save. Saving
/// leaves other keys untouched.
/// </summary>
public sealed class AutoUpdateSettingsStore
{
    private readonly Func<string> _directory;

    public AutoUpdateSettingsStore(Func<string>? directory = null)
        => _directory = directory ?? (() => AppSettingsFile.DefaultDirectory);

    public bool Load() => AppSettingsFile.Load(_directory()).AutoUpdate ?? true;

    /// <summary>False means the choice did not reach disk; the caller keeps the previous state.</summary>
    public bool TrySave(bool enabled)
        => AppSettingsFile.TrySave(_directory(), dto => dto.AutoUpdate = enabled);
}
