using Gauge.Models;

namespace Gauge.Localization;

/// <summary>
/// Localized title/message builders for usage notifications, shared by the evaluator
/// (real alerts) and the developer demo sequence so both read identically in any language.
///
/// The window's own <see cref="UsageWindow.Label"/> is what appears in the title, not a
/// label re-derived from its <see cref="UsageWindowType"/>: GitHub Copilot exposes chat,
/// completions, and premium requests as three billing-cycle windows, so deriving from the
/// type would make their alerts byte-identical.
/// </summary>
public static class NotificationText
{
    public static string ThresholdTitle(
        string toolName,
        UsageWindow window,
        int percent)
        => NormalizeGroupLabel(window.GroupLabel) is { } group
            ? Loc.Format("Notif_ThresholdScopedTitle", toolName, group, window.Label, percent)
            : Loc.Format("Notif_ThresholdTitle", toolName, window.Label, percent);

    public static string ResetTitle(string toolName, UsageWindow window)
        => NormalizeGroupLabel(window.GroupLabel) is { } group
            ? Loc.Format("Notif_ResetScopedTitle", toolName, group, window.Label)
            : Loc.Format("Notif_ResetTitle", toolName, window.Label);

    public static string ResetMessage(double availablePercent)
        => Loc.Format("Notif_ResetMessage", availablePercent);

    private static string? NormalizeGroupLabel(string? value)
        => value?.Trim() is { Length: > 0 } text ? text : null;
}
