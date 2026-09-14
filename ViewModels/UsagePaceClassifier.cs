using Gauge.Localization;
using Gauge.Models;

namespace Gauge.ViewModels;

/// <summary>Compares this window's utilization with evenly spread consumption in its own cycle.</summary>
public static class UsagePaceClassifier
{
    public const double MinimumElapsedRatio = 0.03;
    private const double CautionAheadRatio = 0.10;
    private const double DangerAheadRatio = 0.25;
    /// <summary>Visual placeholder for an incalculable pace; its meaning lives in the tooltip.</summary>
    public const string UnavailableText = "–";

    public static (string Text, UsageLevel Level) ForRow(UsageWindow window, DateTimeOffset? now = null)
    {
        if (window.Duration is not { } duration || duration <= TimeSpan.Zero
            || window.ResetTime is not { } reset || !double.IsFinite(window.UsedRatio))
            return (UnavailableText, UsageLevel.Ok);

        var remaining = reset - (now ?? DateTimeOffset.UtcNow);
        // Subtract durations rather than reset-duration: a malformed huge duration must not
        // underflow DateTimeOffset. Expired or future cycles have no current pace.
        if (remaining <= TimeSpan.Zero || remaining > duration) return (UnavailableText, UsageLevel.Ok);
        var elapsed = duration - remaining;
        var elapsedRatio = elapsed.TotalSeconds / duration.TotalSeconds;
        if (elapsedRatio < MinimumElapsedRatio) return (string.Empty, UsageLevel.Ok);

        // Positive means reserve, negative means deficit, measured in percentage points
        // of the full quota. No other window or history lane participates in this value.
        var ahead = Math.Clamp(window.UsedRatio, 0, 1) - elapsedRatio;
        var difference = (int)Math.Round(-ahead * 100, MidpointRounding.AwayFromZero);
        var text = difference == 0 ? Loc.Get("Pace_OnPace")
            : Loc.Format(difference > 0 ? "Pace_Reserve" : "Pace_Deficit", Math.Abs(difference));
        var level = ahead >= DangerAheadRatio ? UsageLevel.Danger
            : ahead >= CautionAheadRatio ? UsageLevel.Caution : UsageLevel.Ok;
        return (text, level);
    }
}
