using Gauge.Models;

namespace Gauge.Services;

/// <summary>
/// Persists the usage-notification preferences (master switch + per-kind toggles) in
/// <c>%APPDATA%\Gauge\settings.json</c> via <see cref="AppSettingsFile"/>. Every flag
/// defaults to enabled — a missing/absent key reads as on, so a settings file written
/// before a toggle existed keeps the prior behavior. Saving leaves other keys (tool
/// registration, UI language) untouched.
/// </summary>
public sealed class NotificationSettingsStore
{
    private readonly Func<string> _directory;

    public NotificationSettingsStore(Func<string>? directory = null)
        => _directory = directory ?? (() => AppSettingsFile.DefaultDirectory);

    public NotificationPreferences Load()
    {
        var dto = AppSettingsFile.Load(_directory());
        return new NotificationPreferences(
            dto.NotificationsEnabled ?? true,
            dto.NotifyThresholds ?? true,
            dto.NotifyResets ?? true);
    }

    public void Save(NotificationPreferences preferences)
        => AppSettingsFile.Save(_directory(), dto =>
        {
            dto.NotificationsEnabled = preferences.Enabled;
            dto.NotifyThresholds = preferences.Thresholds;
            dto.NotifyResets = preferences.Resets;
        });
}
