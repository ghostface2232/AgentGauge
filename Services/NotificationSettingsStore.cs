using Gauge.Models;

namespace Gauge.Services;

/// <summary>
/// Persists the usage-notification preferences (one toggle per alert kind) in
/// <c>%APPDATA%\Gauge\settings.json</c> via <see cref="AppSettingsFile"/>. Every flag
/// defaults to enabled — a missing/absent key reads as on, so a settings file written
/// before a toggle existed keeps the prior behavior. Saving leaves other keys (tool
/// registration, UI language) untouched.
///
/// <c>NotificationsEnabled</c> is no longer an independent setting (see
/// <see cref="NotificationPreferences"/>), but it is still read and written: read so a file
/// left by an older build with the master paused stays silent instead of suddenly alerting
/// on upgrade, and written as the derived value so downgrading to an older build — which
/// only understands that key — keeps the user's choice.
/// </summary>
public sealed class NotificationSettingsStore
{
    private readonly Func<string> _directory;

    public NotificationSettingsStore(Func<string>? directory = null)
        => _directory = directory ?? (() => AppSettingsFile.DefaultDirectory);

    public NotificationPreferences Load()
    {
        var dto = AppSettingsFile.Load(_directory());
        var legacyMasterPause = dto.NotificationsEnabled == false;
        return new NotificationPreferences(
            !legacyMasterPause && (dto.NotifyThresholds ?? true),
            !legacyMasterPause && (dto.NotifyResets ?? true));
    }

    public void Save(NotificationPreferences preferences)
        => AppSettingsFile.Save(_directory(), dto =>
        {
            dto.NotificationsEnabled = preferences.Enabled;
            dto.NotifyThresholds = preferences.Thresholds;
            dto.NotifyResets = preferences.Resets;
        });
}
