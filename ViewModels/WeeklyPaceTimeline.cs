using Gauge.Models;

namespace Gauge.ViewModels;

/// <summary>
/// The share of a cycle that has "elapsed" when each local weekday carries its own weight —
/// the arithmetic behind the work-day and automatic pace models. A weight of zero on a day
/// means no consumption is expected then, so the expected curve is flat across it; the
/// uniform model is the special case of every weight equal.
/// </summary>
public static class WeeklyPaceTimeline
{
    /// <summary>Weights for a Monday-to-Friday working week, indexed by <see cref="DayOfWeek"/>.</summary>
    public static readonly IReadOnlyList<double> WorkDayWeights = [0, 1, 1, 1, 1, 1, 0];

    /// <summary>
    /// Shortest window a weekday model applies to. A model that distinguishes days has
    /// nothing to say inside a single afternoon, and applying it there would only make a
    /// 5-hour session that straddles midnight into Saturday read as frozen.
    /// </summary>
    public static readonly TimeSpan MinimumDuration = TimeSpan.FromDays(2);

    /// <summary>
    /// Longest window a weekday model applies to — a year, matching the bound the usage
    /// cache accepts. It keeps the day-by-day integration short and rules out a malformed
    /// duration underflowing the cycle start; anything longer uses the uniform spread.
    /// </summary>
    public static readonly TimeSpan MaximumDuration = TimeSpan.FromDays(366);

    /// <summary>
    /// The weighted time from <paramref name="start"/> to <paramref name="now"/> as a share
    /// of the weighted time from <paramref name="start"/> to <paramref name="end"/>, where
    /// each instant is weighted by <paramref name="weights"/>[its local weekday]. Null when
    /// the whole cycle carries no weight (nothing is expected at any point, so no pace can be
    /// read off it) — the caller falls back to the uniform spread.
    /// </summary>
    public static double? ElapsedRatio(
        DateTimeOffset start, DateTimeOffset now, DateTimeOffset end,
        IReadOnlyList<double> weights, TimeZoneInfo zone)
    {
        if (weights.Count != 7 || end <= start) return null;
        var total = WeightedSeconds(start, end, weights, zone);
        if (total <= 0) return null;
        var elapsed = WeightedSeconds(start, now < start ? start : now > end ? end : now, weights, zone);
        return Math.Clamp(elapsed / total, 0, 1);
    }

    // Integrates the weight over [from, to] one local calendar day at a time, so a segment
    // never straddles a weekday change. Local midnight is derived through the zone rather
    // than by adding 24 hours, so a DST day is still one segment.
    private static double WeightedSeconds(DateTimeOffset from, DateTimeOffset to, IReadOnlyList<double> weights, TimeZoneInfo zone)
    {
        var sum = 0.0;
        var cursor = from;
        while (cursor < to)
        {
            var local = TimeZoneInfo.ConvertTime(cursor, zone);
            var nextMidnightLocal = local.Date.AddDays(1);
            var nextMidnight = new DateTimeOffset(nextMidnightLocal, zone.GetUtcOffset(nextMidnightLocal));
            // A zone transition can place the derived midnight at or before the cursor;
            // stepping an hour keeps the walk finite without skipping a day boundary.
            if (nextMidnight <= cursor) nextMidnight = cursor.AddHours(1);
            var segmentEnd = nextMidnight < to ? nextMidnight : to;
            var weight = weights[(int)local.DayOfWeek];
            if (weight > 0) sum += weight * (segmentEnd - cursor).TotalSeconds;
            cursor = segmentEnd;
        }
        return sum;
    }
}
