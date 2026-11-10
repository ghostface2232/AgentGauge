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

        var segments = new List<int> { 0 };
        for (var i = 1; i < recent.Count; i++)
        {
            if (recent[i].IsManualResetAfter(recent[i - 1]))
            {
                segments.Add(i);
            }
            else if (UsageCycleBoundary.IsReset(recent[i - 1], recent[i]))
            {
                // A scheduled rollover still discards the previous cycle's readings.
                segments.Clear();
                segments.Add(i);
            }
        }
        segments.Add(recent.Count);

        var slope = FitSlope(recent, segments);
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

    // Fit a shared rate with a separate starting level for each allowance. The reset
    // interval contributes neither usage nor time, and the projection uses the latest ratio.
    private static double FitSlope(IReadOnlyList<UsageSample> samples, List<int> segments)
    {
        var observedCount = 0;
        var observedSpan = TimeSpan.Zero;
        double covariance = 0, variance = 0;
        for (var segment = 1; segment < segments.Count; segment++)
        {
            var start = segments[segment - 1];
            var end = segments[segment];
            // A window waiting at 100% before a manual reset cannot measure further demand.
            if (segment < segments.Count - 1)
            {
                while (end - start >= 2 && samples[end - 1].UsedRatio >= 1 && samples[end - 2].UsedRatio >= 1)
                    end--;
            }

            var count = end - start;
            if (count < 2) continue;

            var origin = samples[start].CapturedAt;
            observedCount += count;
            observedSpan += samples[end - 1].CapturedAt - origin;
            double sumX = 0, sumY = 0, sumXy = 0, sumXx = 0;
            for (var i = start; i < end; i++)
            {
                var x = (samples[i].CapturedAt - origin).TotalSeconds;
                var y = samples[i].UsedRatio;
                sumX += x;
                sumY += y;
                sumXy += x * y;
                sumXx += x * x;
            }
            // With one segment, keep the original arithmetic exactly.
            var divisor = segments.Count == 2 ? 1 : count;
            covariance += (count * sumXy - sumX * sumY) / divisor;
            variance += (count * sumXx - sumX * sumX) / divisor;
        }
        return observedCount >= MinimumSamples && observedSpan >= MinimumSpan && variance > 0
            ? covariance / variance : double.NaN;
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
