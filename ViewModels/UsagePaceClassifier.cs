using Gauge.Localization;
using Gauge.Models;

namespace Gauge.ViewModels;

/// <summary>Compares this window's utilization with evenly spread consumption in its own cycle.</summary>
public static class UsagePaceClassifier
{
    public const double MinimumElapsedRatio = 0.03;
    /// <summary>Visual placeholder for an incalculable pace; its meaning lives in the tooltip.</summary>
    public const string UnavailableText = "–";

    /// <summary>
    /// The pace caption for a row. Text only: the caption is deliberately not coloured by
    /// severity, because a row can be burning fast while still mostly full, and a green
    /// "burning fast" (or an amber one beside a green bar) read as contradictory. The bar's
    /// own level colour stays the single severity signal.
    /// </summary>
    public static string ForRow(UsageWindow window, DateTimeOffset? now = null)
    {
        if (window.Duration is not { } duration || duration <= TimeSpan.Zero
            || window.ResetTime is not { } reset || !double.IsFinite(window.UsedRatio))
            return UnavailableText;

        var remaining = reset - (now ?? DateTimeOffset.UtcNow);
        // Subtract durations rather than reset-duration: a malformed huge duration must not
        // underflow DateTimeOffset. Expired or future cycles have no current pace.
        if (remaining <= TimeSpan.Zero || remaining > duration) return UnavailableText;
        var elapsed = duration - remaining;
        var elapsedRatio = elapsed.TotalSeconds / duration.TotalSeconds;
        if (elapsedRatio < MinimumElapsedRatio) return string.Empty;
        // An exhausted window has no pace left to describe — "burning fast" beside 100% only
        // restates the bar — so the caption is dropped and the ETA/reset lines carry the row.
        if (window.UsedRatio >= 1) return string.Empty;

        // Positive means reserve, negative means deficit, measured in percentage points
        // of the full quota. No other window or history lane participates in this value.
        var ahead = Math.Clamp(window.UsedRatio, 0, 1) - elapsedRatio;
        var difference = (int)Math.Round(-ahead * 100, MidpointRounding.AwayFromZero);
        return difference == 0 ? Loc.Get("Pace_OnPace")
            : Loc.Format(difference > 0 ? "Pace_Reserve" : "Pace_Deficit", Math.Abs(difference));
    }
}
