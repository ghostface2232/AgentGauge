using Gauge.Localization;
using Gauge.Models;

namespace Gauge.ViewModels;

/// <summary>
/// Projects when a window will hit 100% from its recorded recent readings (the usage
/// history), as a restrained caption ("2시간 후 소진 예상"). Unlike
/// <see cref="UsagePaceClassifier"/> — which compares utilization against evenly spread
/// usage over the whole cycle — this extrapolates the measured short-term burn rate, so
/// it reacts to what the user is doing right now.
///
/// It stays silent unless the projection is trustworthy and actionable: at least three
/// live readings spanning 30+ minutes, a positive slope, and a projected exhaustion that
/// lands before the window's reset (running out after the reset is not a problem).
/// </summary>
public static class UsageEtaClassifier
{
    /// <summary>Trailing span of history the projection is computed from.</summary>
    public static readonly TimeSpan Lookback = TimeSpan.FromMinutes(90);

    private static readonly TimeSpan MinimumSpan = TimeSpan.FromMinutes(30);
    private const int MinimumSamples = 3;
    private const double MinimumUsedRatio = 0.05;

    /// <summary>
    /// A ratio drop of at least this much between consecutive samples is a cycle reset;
    /// earlier samples are discarded so a fresh cycle never averages against the old one.
    /// Smaller decreases are provider jitter/rounding and are left to the regression.
    /// </summary>
    private const double ResetDropThreshold = 0.05;

    /// <summary>ETA caption for a row, or empty when the projection should stay quiet.</summary>
    public static string ForRow(
        UsageWindow window, IReadOnlyList<UsageSample> samples, DateTimeOffset? now = null)
    {
        var current = now ?? DateTimeOffset.Now;
        var used = Math.Clamp(window.UsedRatio, 0.0, 1.0);
        if (used < MinimumUsedRatio || used >= 1.0)
        {
            return string.Empty;
        }
        if (ProjectExhaustion(samples, current) is not { } eta)
        {
            return string.Empty;
        }
        // Only warn when exhaustion lands before the reset; an unknown reset time gives no
        // basis to call the pace a problem, so it also stays quiet.
        if (window.ResetTime is not { } reset || eta >= reset)
        {
            return string.Empty;
        }
        return Format(eta - current);
    }

    /// <summary>
    /// Projected exhaustion time from a least-squares fit over the recent samples, or null
    /// when there is no trustworthy upward trend.
    /// </summary>
    public static DateTimeOffset? ProjectExhaustion(IReadOnlyList<UsageSample> samples, DateTimeOffset now)
    {
        var cutoff = now - Lookback;
        var recent = samples.Where(s => s.CapturedAt >= cutoff && s.CapturedAt <= now).ToList();

        var start = 0;
        for (var i = 1; i < recent.Count; i++)
        {
            if (recent[i].UsedRatio < recent[i - 1].UsedRatio - ResetDropThreshold)
            {
                start = i;
            }
        }
        if (start > 0)
        {
            recent = recent.Skip(start).ToList();
        }

        if (recent.Count < MinimumSamples
            || recent[^1].CapturedAt - recent[0].CapturedAt < MinimumSpan)
        {
            return null;
        }

        // Least-squares slope in ratio-per-second over (time, ratio). More stable than the
        // endpoint difference when readings arrive at uneven intervals.
        var t0 = recent[0].CapturedAt;
        double sumX = 0, sumY = 0, sumXy = 0, sumXx = 0;
        foreach (var sample in recent)
        {
            var x = (sample.CapturedAt - t0).TotalSeconds;
            var y = sample.UsedRatio;
            sumX += x;
            sumY += y;
            sumXy += x * y;
            sumXx += x * x;
        }
        var n = recent.Count;
        var denominator = n * sumXx - sumX * sumX;
        if (denominator <= 0)
        {
            return null;
        }
        var slope = (n * sumXy - sumX * sumY) / denominator;
        if (!double.IsFinite(slope) || slope <= 0)
        {
            return null;
        }

        var last = recent[^1];
        var secondsToFull = (1.0 - Math.Clamp(last.UsedRatio, 0.0, 1.0)) / slope;
        if (!double.IsFinite(secondsToFull) || secondsToFull <= 0
            || secondsToFull > TimeSpan.FromDays(30).TotalSeconds)
        {
            return null;
        }

        var eta = last.CapturedAt + TimeSpan.FromSeconds(secondsToFull);
        // A projection already in the past means the fit overshot the clamped ratio;
        // the percent and pace warning cover that state, so stay quiet.
        return eta > now ? eta : null;
    }

    private static string Format(TimeSpan untilEta)
    {
        if (untilEta.TotalHours >= 24)
        {
            var days = Math.Max(1, (int)Math.Ceiling(untilEta.TotalDays));
            return Loc.Format(days == 1 ? "Eta_InDay" : "Eta_InDays", days);
        }
        var hours = (int)untilEta.TotalHours;
        var minutes = untilEta.Minutes;
        if (hours > 0 && minutes > 0)
        {
            return Loc.Format("Eta_InHoursMinutes", hours, minutes);
        }
        if (hours > 0)
        {
            return Loc.Format("Eta_InHours", hours);
        }
        return Loc.Format("Eta_InMinutes", Math.Max(1, minutes));
    }
}
