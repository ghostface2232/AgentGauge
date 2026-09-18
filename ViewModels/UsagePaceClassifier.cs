using Gauge.Localization;
using Gauge.Models;

namespace Gauge.ViewModels;

/// <summary>Compares this window's utilization with the expected consumption curve in its own cycle.</summary>
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
    /// <param name="model">
    /// How the cycle's quota is expected to spread across its days (see
    /// <see cref="WeeklyPaceModel"/>). Only windows of at least
    /// <see cref="WeeklyPaceTimeline.MinimumDuration"/> honour it; shorter ones always use
    /// the uniform spread.
    /// </param>
    /// <param name="profile">
    /// This window's recorded per-weekday consumption, consulted only by
    /// <see cref="WeeklyPaceModel.Automatic"/>; an absent or insufficient profile reads as
    /// uniform.
    /// </param>
    /// <param name="zone">Zone whose calendar days the weekday models count; local by default.</param>
    public static string ForRow(
        UsageWindow window, DateTimeOffset? now = null,
        WeeklyPaceModel model = WeeklyPaceModel.Uniform, UsageWeekdayProfile? profile = null,
        TimeZoneInfo? zone = null)
    {
        if (window.Duration is not { } duration || duration <= TimeSpan.Zero
            || window.ResetTime is not { } reset || !double.IsFinite(window.UsedRatio))
            return UnavailableText;

        var current = now ?? DateTimeOffset.UtcNow;
        var remaining = reset - current;
        // Subtract durations rather than reset-duration: a malformed huge duration must not
        // underflow DateTimeOffset. Expired or future cycles have no current pace.
        if (remaining <= TimeSpan.Zero || remaining > duration) return UnavailableText;
        var elapsed = duration - remaining;
        var elapsedRatio = elapsed.TotalSeconds / duration.TotalSeconds;
        // The early-silence gate is wall-clock: it exists to keep the first hours of any cycle
        // quiet, and a weekday model that expects nothing over a weekend must not reopen it.
        if (elapsedRatio < MinimumElapsedRatio) return string.Empty;
        // An exhausted window has no pace left to describe — "burning fast" beside 100% only
        // restates the bar — so the caption is dropped and the ETA/reset lines carry the row.
        if (window.UsedRatio >= 1) return string.Empty;

        var expectedRatio = ExpectedRatio(current, reset, duration, model, profile, zone) ?? elapsedRatio;

        // Positive means reserve, negative means deficit, measured in percentage points
        // of the full quota. No other window or history lane participates in this value.
        var ahead = Math.Clamp(window.UsedRatio, 0, 1) - expectedRatio;
        var difference = (int)Math.Round(-ahead * 100, MidpointRounding.AwayFromZero);
        return difference == 0 ? Loc.Get("Pace_OnPace")
            : Loc.Format(difference > 0 ? "Pace_Reserve" : "Pace_Deficit", Math.Abs(difference));
    }

    // The share of the cycle's quota expected to be spent by now under the chosen model, or
    // null where the model does not apply and the uniform spread stands.
    private static double? ExpectedRatio(
        DateTimeOffset now, DateTimeOffset reset, TimeSpan duration,
        WeeklyPaceModel model, UsageWeekdayProfile? profile, TimeZoneInfo? zone)
    {
        if (duration < WeeklyPaceTimeline.MinimumDuration || duration > WeeklyPaceTimeline.MaximumDuration) return null;
        // The cycle start is derived only for a duration inside the bounds above, so the
        // subtraction cannot underflow DateTimeOffset (the caller's invariant) and the
        // day-by-day walk in WeeklyPaceTimeline stays short.
        var start = reset - duration;
        var weights = model switch
        {
            WeeklyPaceModel.WorkDays => WeeklyPaceTimeline.WorkDayWeights,
            WeeklyPaceModel.Automatic when profile is { IsSufficient: true } => profile.Weights,
            _ => null,
        };
        return weights is null ? null
            : WeeklyPaceTimeline.ElapsedRatio(start, now, reset, weights, zone ?? TimeZoneInfo.Local);
    }
}
