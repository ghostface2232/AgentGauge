namespace Gauge.Services;

/// <summary>
/// Persists whether usage notifications are enabled, in <c>%APPDATA%\Gauge\settings.json</c>
/// via <see cref="AppSettingsFile"/>. The default is enabled — a missing/absent key reads
/// as on, so a settings file written before this toggle existed keeps the prior behavior.
/// Saving leaves other keys (tool registration, UI language) untouched.
/// </summary>
public sealed class NotificationSettingsStore
{
    private readonly Func<string> _directory;

    public NotificationSettingsStore(Func<string>? directory = null)
        => _directory = directory ?? (() => AppSettingsFile.DefaultDirectory);

    public bool Load() => AppSettingsFile.Load(_directory()).NotificationsEnabled ?? true;

    public void Save(bool enabled)
        => AppSettingsFile.Save(_directory(), dto => dto.NotificationsEnabled = enabled);
}
