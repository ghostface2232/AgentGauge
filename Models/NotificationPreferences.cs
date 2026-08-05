namespace Gauge.Models;

/// <summary>
/// User preferences for usage notifications: one toggle per alert kind, each mirrored in
/// both the tray menu and the settings card.
///
/// There is deliberately no separate master switch. It used to exist only in the tray, so
/// pausing there left the settings card showing two switches that were still "on" while
/// nothing was delivered — a state the UI could not express. <see cref="Enabled"/> is now
/// derived: notifications are silent exactly when both kinds are off, which is what the
/// master pause meant anyway, and no surface can disagree with another.
/// </summary>
public readonly record struct NotificationPreferences(bool Thresholds, bool Resets)
{
    public static NotificationPreferences Default => new(true, true);

    /// <summary>
    /// Whether any alert can be raised at all. Gates detection in
    /// <c>UsageNotificationService.Process</c>, so turning the last kind off also stops the
    /// evaluator from consuming crossings.
    /// </summary>
    public bool Enabled => Thresholds || Resets;

    /// <summary>Whether the given kind is delivered.</summary>
    public bool Allows(UsageNotificationKind kind) => kind switch
    {
        UsageNotificationKind.Threshold => Thresholds,
        UsageNotificationKind.Reset => Resets,
        _ => true,
    };

    /// <summary>Returns a copy with one kind flipped, leaving the other untouched.</summary>
    public NotificationPreferences With(UsageNotificationKind kind, bool enabled) => kind switch
    {
        UsageNotificationKind.Threshold => this with { Thresholds = enabled },
        UsageNotificationKind.Reset => this with { Resets = enabled },
        _ => this,
    };
}
