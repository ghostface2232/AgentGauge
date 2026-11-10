using Gauge.Models;
using Gauge.ViewModels;

namespace Gauge.Tests;

public sealed class UsageDailyBreakdownTests
{
    private static readonly DateTimeOffset Today = new(2026, 11, 11, 11, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Reset = Today.AddDays(2);

    [Fact]
    public void OnlySameDayIncreasesAreShownWhenHistoryStartsLateOrCrossesMidnight()
    {
        var segments = UsageDailyBreakdown.Build(Weekly(0.50),
            [Sample(-26, 0.10), Sample(-24, 0.25), Sample(-2, 0.40), Sample(0, 0.50)],
            Today, TimeZoneInfo.Utc);

        Assert.Collection(segments,
            first => AssertSegment(first, new DateOnly(2026, 11, 10), 0.15),
            second => AssertSegment(second, new DateOnly(2026, 11, 11), 0.10));
        Assert.Equal(0.25, UsageDailyBreakdown.UnattributedUsedRatio(0.50, segments.Sum(s => s.UsedRatio)), 8);
        Assert.Empty(UsageDailyBreakdown.Build(Weekly(0.50), [], Today, TimeZoneInfo.Utc));
    }

    [Fact]
    public void ManualResetCutsOffEarlierUsageEvenWithTheSameScheduledReset()
    {
        var segments = UsageDailyBreakdown.Build(Weekly(0.35),
            [Sample(-26, 0.60, 1), Sample(-24, 0.80, 1), Sample(-2, 0.12, 0), Sample(0, 0.35, 0)],
            Today, TimeZoneInfo.Utc);

        AssertSegment(Assert.Single(segments), new DateOnly(2026, 11, 11), 0.23);
    }

    [Fact]
    public void RecalculationDoesNotInflateTheDailyShares()
    {
        var segments = UsageDailyBreakdown.Build(Weekly(0.35),
            [Sample(-26, 0), Sample(-24, 0.60), Sample(-2, 0.30), Sample(0, 0.35)],
            Today, TimeZoneInfo.Utc);

        Assert.Collection(segments,
            first => AssertSegment(first, new DateOnly(2026, 11, 10), 0.30),
            second => AssertSegment(second, new DateOnly(2026, 11, 11), 0.05));
        Assert.Equal(0, UsageDailyBreakdown.UnattributedUsedRatio(0.35, segments.Sum(s => s.UsedRatio)), 8);
    }

    private static UsageWindow Weekly(double used) => new()
    {
        Type = UsageWindowType.Weekly, Label = "Weekly", UsedRatio = used, ResetTime = Reset,
    };

    private static UsageSample Sample(int hours, double used, int? credits = null)
        => new(Today.AddHours(hours), used, Reset, credits);

    private static void AssertSegment(DailyUsageSegment segment, DateOnly date, double ratio)
    {
        Assert.Equal(date, segment.Day);
        Assert.Equal(ratio, segment.UsedRatio, 8);
    }
}
