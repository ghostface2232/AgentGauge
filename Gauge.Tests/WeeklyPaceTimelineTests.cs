using Gauge.ViewModels;

namespace Gauge.Tests;

/// <summary>
/// The weekday-weighted elapsed share behind the work-day and automatic pace models: a zero
/// weight flattens the curve across that day, equal weights reproduce the uniform spread, and
/// a cycle with no weight at all yields nothing rather than a division by zero.
/// </summary>
public sealed class WeeklyPaceTimelineTests
{
    // Monday 00:00 → next Monday 00:00, UTC, so local weekdays are unambiguous.
    private static readonly DateTimeOffset Monday = new(2026, 8, 3, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset NextMonday = Monday.AddDays(7);
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;

    [Theory]
    [InlineData(0.0, 0.0)]    // Monday 00:00 — nothing elapsed
    [InlineData(2.0, 0.4)]    // Wednesday 00:00 — two of five work days
    [InlineData(2.5, 0.5)]    // Wednesday noon
    [InlineData(5.0, 1.0)]    // Saturday 00:00 — the week's work time is spent
    [InlineData(5.5, 1.0)]    // Saturday noon — flat across the weekend
    [InlineData(6.9, 1.0)]    // Sunday evening
    public void WorkDayWeightsFreezeTheWeekend(double daysIn, double expected)
    {
        var ratio = WeeklyPaceTimeline.ElapsedRatio(Monday, Monday.AddDays(daysIn), NextMonday, WeeklyPaceTimeline.WorkDayWeights, Utc);
        Assert.Equal(expected, ratio!.Value, 6);
    }

    [Theory]
    [InlineData(1.75)]
    [InlineData(3.0)]
    [InlineData(6.25)]
    public void EqualWeightsReproduceTheUniformSpread(double daysIn)
    {
        var equal = new double[] { 1, 1, 1, 1, 1, 1, 1 };
        var ratio = WeeklyPaceTimeline.ElapsedRatio(Monday, Monday.AddDays(daysIn), NextMonday, equal, Utc);
        Assert.Equal(daysIn / 7, ratio!.Value, 6);
    }

    [Fact]
    public void WeightsAreRelativeNotAbsolute()
    {
        // A profile that is simply taller (more cycles accumulated) has the same shape.
        var shape = new double[] { 0, 3, 1, 1, 1, 0, 0 };
        var taller = shape.Select(w => w * 8).ToArray();
        var now = Monday.AddDays(1.5);
        Assert.Equal(
            WeeklyPaceTimeline.ElapsedRatio(Monday, now, NextMonday, shape, Utc),
            WeeklyPaceTimeline.ElapsedRatio(Monday, now, NextMonday, taller, Utc));
        // Monday carries half the week's weight, so half of Monday is a quarter of the cycle.
        Assert.Equal(0.25, WeeklyPaceTimeline.ElapsedRatio(Monday, Monday.AddHours(12), NextMonday, shape, Utc)!.Value, 6);
    }

    [Fact]
    public void CountsDaysInTheGivenZoneNotUtc()
    {
        // UTC+9: Friday 20:00 UTC is already Saturday 05:00 in Seoul, so a work-day model in
        // that zone has finished the week, while the UTC reading has five hours to go.
        var seoul = TimeZoneInfo.CreateCustomTimeZone("KST", TimeSpan.FromHours(9), "KST", "KST");
        var fridayEveningUtc = Monday.AddDays(4).AddHours(20);
        var start = new DateTimeOffset(2026, 8, 3, 0, 0, 0, TimeSpan.FromHours(9));
        var end = start.AddDays(7);
        Assert.Equal(1.0, WeeklyPaceTimeline.ElapsedRatio(start, fridayEveningUtc, end, WeeklyPaceTimeline.WorkDayWeights, seoul)!.Value, 6);
        Assert.True(WeeklyPaceTimeline.ElapsedRatio(start, fridayEveningUtc, end, WeeklyPaceTimeline.WorkDayWeights, Utc)!.Value < 1.0);
    }

    [Fact]
    public void ClampsNowToTheCycle()
    {
        Assert.Equal(0.0, WeeklyPaceTimeline.ElapsedRatio(Monday, Monday.AddDays(-3), NextMonday, WeeklyPaceTimeline.WorkDayWeights, Utc));
        Assert.Equal(1.0, WeeklyPaceTimeline.ElapsedRatio(Monday, NextMonday.AddDays(2), NextMonday, WeeklyPaceTimeline.WorkDayWeights, Utc));
    }

    [Fact]
    public void NoWeightAnywhereYieldsNothing()
    {
        // A cycle expecting no consumption at any point has no curve to compare against;
        // the caller falls back to the uniform spread instead of dividing by zero.
        Assert.Null(WeeklyPaceTimeline.ElapsedRatio(Monday, Monday.AddDays(3), NextMonday, new double[7], Utc));
        // A weekend-only window under the work-day model is the same case.
        var saturday = Monday.AddDays(5);
        Assert.Null(WeeklyPaceTimeline.ElapsedRatio(saturday, saturday.AddDays(1), saturday.AddDays(2), WeeklyPaceTimeline.WorkDayWeights, Utc));
        // Malformed inputs never throw.
        Assert.Null(WeeklyPaceTimeline.ElapsedRatio(Monday, Monday, Monday, WeeklyPaceTimeline.WorkDayWeights, Utc));
        Assert.Null(WeeklyPaceTimeline.ElapsedRatio(Monday, Monday, NextMonday, new double[] { 1, 1 }, Utc));
    }
}
