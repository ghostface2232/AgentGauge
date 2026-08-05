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
///
/// Known one-time cost of that migration: a legacy file that paused the master while both
/// kinds were on reads as both-off, so re-arming one kind persists the other as off and the
/// pre-pause per-kind values are gone. Accepted deliberately — both kinds default to on, so
/// there is almost never a non-default pair to lose, and the alternative (one click silently
/// arming two kinds) is the more surprising of the two.
/// </summary>
public sealed class NotificationSettingsStore
{
    private readonly Func<string> _directory;

    public NotificationSettingsStore(Func<string>? directory = null)
        => _directory = directory ?? (() => AppSettingsFile.DefaultDirectory);

    public NotificationPreferences Load()
    {
        _ = TryLoad(out var preferences);
        return preferences;
    }

    /// <summary>
    /// The result-reporting form of <see cref="Load"/>. False means settings.json exists but
    /// could not be read, so the returned value is the all-enabled default rather than the
    /// user's choice. A caller that would ACT on it (arm the notification service, flip the
    /// tray checkmarks) must keep its current state instead — adopting the default would
    /// silently un-mute alerts the user turned off.
    /// </summary>
    public bool TryLoad(out NotificationPreferences preferences)
    {
        var read = AppSettingsFile.TryLoad(_directory(), out var dto);
        var legacyMasterPause = dto.NotificationsEnabled == false;
        preferences = new NotificationPreferences(
            !legacyMasterPause && (dto.NotifyThresholds ?? true),
            !legacyMasterPause && (dto.NotifyResets ?? true));
        return read;
    }

    public void Save(NotificationPreferences preferences) => _ = TrySave(preferences);

    /// <summary>
    /// The result-reporting form of <see cref="Save"/>, for the caller that must know
    /// whether the preference actually reached disk before telling the user it did.
    /// </summary>
    public bool TrySave(NotificationPreferences preferences)
        => AppSettingsFile.TrySave(_directory(), dto =>
        {
            dto.NotificationsEnabled = preferences.Enabled;
            dto.NotifyThresholds = preferences.Thresholds;
            dto.NotifyResets = preferences.Resets;
        });
}
