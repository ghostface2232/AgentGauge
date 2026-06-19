using Gauge.Models;

namespace Gauge.Localization;

/// <summary>
/// Localized title/message builders for usage notifications, shared by the evaluator
/// (real alerts) and the developer demo sequence so both read identically in any language.
/// </summary>
public static class NotificationText
{
    public static string ThresholdTitle(string toolName, UsageWindowType type, int percent)
        => Loc.Format("Notif_ThresholdTitle", toolName, WindowLabels.For(type), percent);

    public static string ResetTitle(string toolName, UsageWindowType type)
        => Loc.Format("Notif_ResetTitle", toolName, WindowLabels.For(type));

    public static string ResetMessage(double availablePercent)
        => Loc.Format("Notif_ResetMessage", availablePercent);
}
