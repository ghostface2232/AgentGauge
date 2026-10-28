namespace Gauge.Models;

/// <summary>
/// What the tray icon draws when a single tool is chosen as the status target: the tool's
/// logo inside a ring split into a top arc (the short, 5-hour window) and a bottom arc (the
/// long window — weekly, or a billing cycle). A null ratio leaves that arc as a bare track.
/// </summary>
public sealed record StatusRingState(ToolKind Kind, string ToolName, UsageWindow? Short, UsageWindow? Long)
{
    /// <summary>The higher of the two arcs' used shares; drives the level shown by the arcs.</summary>
    public double? HighestUsedRatio => new[] { Short?.UsedRatio, Long?.UsedRatio }
        .Where(r => r is { } v && double.IsFinite(v))
        .Max();
}

/// <summary>
/// Picks the two windows a tool's status ring shows. Pure, so both the tray renderer and the
/// tooltip agree on what the arcs mean.
/// </summary>
public static class StatusRingSelection
{
    /// <summary>
    /// The short arc is the 5-hour window and the long arc the weekly one, both from the
    /// tool's primary family: account-wide windows when the tool reports any, otherwise the
    /// first family that has a 5-hour or weekly window (Antigravity reports only
    /// family-scoped windows). Scoped extras — Claude's Fable weekly, Antigravity's Gemini 4
    /// Argon, Codex's named model limits — therefore never reach the ring. A tool billed per
    /// cycle (Cursor, GitHub Copilot) has no rolling windows, so its most-used billing-cycle
    /// window fills the long arc and the short arc stays empty.
    /// </summary>
    public static (UsageWindow? Short, UsageWindow? Long) Select(IReadOnlyList<UsageWindow> windows)
    {
        var rolling = windows
            .Where(w => w.Type is UsageWindowType.FiveHour or UsageWindowType.Weekly && double.IsFinite(w.UsedRatio))
            .ToList();
        if (rolling.Count > 0)
        {
            var family = rolling.Any(w => string.IsNullOrEmpty(w.GroupLabel))
                ? null
                : rolling[0].GroupLabel;
            var inFamily = rolling.Where(w => string.Equals(
                string.IsNullOrEmpty(w.GroupLabel) ? null : w.GroupLabel, family, StringComparison.Ordinal)).ToList();
            return (inFamily.FirstOrDefault(w => w.Type == UsageWindowType.FiveHour),
                    inFamily.FirstOrDefault(w => w.Type == UsageWindowType.Weekly));
        }

        var billing = windows
            .Where(w => w.Type == UsageWindowType.BillingCycle && double.IsFinite(w.UsedRatio))
            .OrderByDescending(w => w.UsedRatio)
            .FirstOrDefault();
        return (null, billing);
    }
}
