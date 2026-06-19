using Gauge.Models;

namespace Gauge.Localization;

/// <summary>
/// Localized short labels for each <see cref="UsageWindowType"/> (e.g. "5-hour",
/// "Weekly"). Providers call this when building a <see cref="UsageWindow"/> so the label
/// is always in the active language; the popover and notifications then reuse it.
/// </summary>
public static class WindowLabels
{
    public static string For(UsageWindowType type) => type switch
    {
        UsageWindowType.FiveHour => Loc.Get("Label_FiveHour"),
        UsageWindowType.Weekly => Loc.Get("Label_Weekly"),
        UsageWindowType.BillingCycle => Loc.Get("Label_BillingCycle"),
        UsageWindowType.ModelQuota => Loc.Get("Label_ModelQuota"),
        _ => Loc.Get("Label_Weekly"),
    };
}
