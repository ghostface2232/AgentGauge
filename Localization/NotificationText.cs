using Gauge.Models;

namespace Gauge.Localization;

/// <summary>
/// Localized title/message builders for usage notifications, shared by the evaluator
/// (real alerts) and the developer demo sequence so both read identically in any language.
/// </summary>
public static class NotificationText
{
    public static string ThresholdTitle(
        string toolName,
        UsageWindowType type,
        int percent,
        string? groupLabel = null)
    {
        var label = WindowLabels.For(type);
        return NormalizeGroupLabel(groupLabel) is { } group
            ? Loc.Format("Notif_ThresholdScopedTitle", toolName, group, label, percent)
            : Loc.Format("Notif_ThresholdTitle", toolName, label, percent);
    }

    public static string ResetTitle(
        string toolName,
        UsageWindowType type,
        string? groupLabel = null)
    {
        var label = WindowLabels.For(type);
        return NormalizeGroupLabel(groupLabel) is { } group
            ? Loc.Format("Notif_ResetScopedTitle", toolName, group, label)
            : Loc.Format("Notif_ResetTitle", toolName, label);
    }

    public static string ResetMessage(double availablePercent)
        => Loc.Format("Notif_ResetMessage", availablePercent);

    private static string? NormalizeGroupLabel(string? value)
        => value?.Trim() is { Length: > 0 } text ? text : null;
}
