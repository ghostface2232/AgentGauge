namespace Gauge.Models;

/// <summary>
/// User preferences for usage notifications: the master switch (also mirrored in the tray
/// menu) plus per-kind toggles. Kind toggles only filter presentation — detection keeps
/// running so re-enabling a kind never replays crossings that happened while it was off.
/// </summary>
public readonly record struct NotificationPreferences(bool Enabled, bool Thresholds, bool Resets)
{
    public static NotificationPreferences Default => new(true, true, true);
}
