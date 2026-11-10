using Gauge.Models;

namespace Gauge.ViewModels;

/// <summary>An observed share of the current weekly usage on one local date.</summary>
public sealed record DailyUsageSegment(DateOnly Day, double UsedRatio);

/// <summary>Partitions the reported fill using observed, same-day quota increases.</summary>
public static class UsageDailyBreakdown
{
    public static readonly TimeSpan Lookback = TimeSpan.FromDays(8);
    private static readonly TimeSpan MaximumGap = TimeSpan.FromHours(12);

    public static IReadOnlyList<DailyUsageSegment> Build(
        UsageWindow window, IReadOnlyList<UsageSample> samples, DateTimeOffset capturedAt,
        TimeZoneInfo? zone = null)
    {
        if (window.Type != UsageWindowType.Weekly || !double.IsFinite(window.UsedRatio)
            || window.UsedRatio <= 0 || window.ResetTime is not { } reset || reset <= capturedAt)
        {
            return [];
        }

        var duration = window.Duration ?? TimeSpan.FromDays(7);
        if (duration <= TimeSpan.Zero || duration > Lookback) return [];
        var start = reset - duration;
        zone ??= TimeZoneInfo.Local;
        var readings = samples.Where(s => s.CapturedAt >= start && s.CapturedAt <= capturedAt
                && double.IsFinite(s.UsedRatio))
            .OrderBy(s => s.CapturedAt).DistinctBy(s => s.CapturedAt).ToList();
        if (readings.Count == 0 || readings[^1].CapturedAt < capturedAt)
        {
            readings.Add(new UsageSample(capturedAt, window.UsedRatio, reset, window.ResetCredits));
        }

        var first = 0;
        for (var i = 1; i < readings.Count; i++)
        {
            if (UsageCycleBoundary.IsReset(readings[i - 1], readings[i])
                || readings[i].IsManualResetAfter(readings[i - 1])) first = i;
        }

        var days = new SortedDictionary<DateOnly, double>();
        var used = Math.Clamp(window.UsedRatio, 0, 1);
        var ceiling = used;
        // Walk backwards so a server correction cannot leave daily shares above today's
        // reported fill. Earlier peaks above a later reading contribute nothing.
        for (var i = readings.Count - 1; i > first; i--)
        {
            var previous = readings[i - 1];
            var current = readings[i];
            var priorRatio = Math.Clamp(previous.UsedRatio, 0, 1);
            var increase = Math.Max(0, ceiling - priorRatio);
            ceiling = Math.Min(ceiling, priorRatio);
            var day = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(current.CapturedAt, zone).Date);
            var previousDay = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(previous.CapturedAt, zone).Date);
            // A midnight or a long observation gap gives no evidence of which day spent
            // the difference. Keep that share unallocated instead of inventing a split.
            if (increase > 0 && day == previousDay && current.CapturedAt - previous.CapturedAt <= MaximumGap)
            {
                days[day] = days.GetValueOrDefault(day) + increase;
            }
        }

        return days.Select(day => new DailyUsageSegment(day.Key, day.Value)).ToList();
    }
}
