using Gauge.Models;
using Gauge.ViewModels;

namespace Gauge.Tests;

public sealed class UsageBurndownTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 14, 12, 0, 0, TimeSpan.Zero);
    private static UsageWindow Window => new() { Type = UsageWindowType.FiveHour, Label = "Session", UsedRatio = .6,
        Duration = TimeSpan.FromHours(5), ResetTime = Now.AddHours(2) };
    private static UsageSample[] Samples => [new(Now.AddHours(-2), .2), new(Now.AddHours(-1), .4), new(Now, .6)];

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void InsufficientHistoryLeavesNoPlot(int count) =>
        Assert.Empty(UsageBurndown.Build(Window, Samples.Take(count).ToList(), Now));

    [Fact]
    public void RemainingAndIdealShareTimeAxis()
    {
        var points = UsageBurndown.Build(Window, Samples, Now);
        Assert.Equal(3, points.Count);
        Assert.Equal(0, points[0].X);
        Assert.Equal(1, points[2].X);
        Assert.Equal(.8, points[0].Remaining, 8);
        Assert.Equal(.4, points[2].Remaining, 8);
        Assert.Equal(.4, points[2].IdealRemaining!.Value, 8);
        Assert.Equal(points[1], UsageBurndown.Nearest(points, .55));
    }

    [Fact]
    public void ResetAndInvalidReadingsNeverJoinCycles()
    {
        Assert.Empty(UsageBurndown.Build(Window, [.. Samples, new(Now.AddSeconds(1), .1)], Now.AddSeconds(1)));
        Assert.Empty(UsageBurndown.Build(Window, [Samples[0], Samples[1], new(Now, double.NaN)], Now));
        Assert.Empty(UsageBurndown.Build(Window with { ResetTime = Now.AddHours(4.5) }, Samples, Now));
    }

    [Fact]
    public void RecalculatedUsageKeepsHistoryButARolledCycleSplitsIt()
    {
        // Duration is left out so only the reset-boundary rule is under test; the separate
        // cycle-start filter needs a duration and would otherwise trim the same samples.
        var window = Window with { Duration = null, UsedRatio = .419 };
        var reset = Now.AddHours(2);
        UsageSample[] samples =
        [
            new(Now.AddHours(-2), .400, reset), new(Now.AddHours(-1), .421, reset), new(Now, .419, reset),
        ];
        // 42.1% → 41.9% on the same reset is the provider recalculating, not a new cycle.
        Assert.Equal(3, UsageBurndown.Build(window, samples, Now).Count);

        samples[2] = samples[2] with { ResetTime = reset.AddHours(5) };
        Assert.Empty(UsageBurndown.Build(window, samples, Now));

        // Even a collapse under one unchanged reset is a recalculation, not a new cycle.
        Assert.Equal(3, UsageBurndown.Build(
            window, [new(Now.AddHours(-2), .40, reset), new(Now.AddHours(-1), .52, reset), new(Now, .07, reset)],
            Now).Count);

        // Without reset timestamps only a drop past the threshold identifies a new cycle.
        Assert.Equal(3, UsageBurndown.Build(
            window, [new(Now.AddHours(-2), .40), new(Now.AddHours(-1), .421), new(Now, .419)], Now).Count);
        Assert.Empty(UsageBurndown.Build(
            window, [new(Now.AddHours(-2), .40), new(Now.AddHours(-1), .50), new(Now, .44)], Now));
    }

    [Fact]
    public void ExpiredSnapshotKeepsCycleStartFilterWithoutIdealLine()
    {
        var start = Now.AddHours(-1);
        var window = Window with { ResetTime = start.AddHours(5) };
        UsageSample[] samples =
        [
            new(start.AddMinutes(-45), .1), new(start.AddMinutes(-30), .2), new(start.AddMinutes(-15), .3),
            new(start.AddMinutes(10), .4), new(start.AddMinutes(20), .5), new(start.AddMinutes(30), .6),
        ];
        var before = UsageBurndown.Build(window, samples, window.ResetTime!.Value.AddSeconds(-1));
        Assert.Equal(3, before.Count);
        Assert.All(before, p => Assert.NotNull(p.IdealRemaining));
        var after = UsageBurndown.Build(window, samples, window.ResetTime!.Value.AddSeconds(1));
        Assert.Equal(3, after.Count);
        Assert.Equal(before.Select(p => p.Sample), after.Select(p => p.Sample));
        Assert.All(after, p => Assert.Null(p.IdealRemaining));
    }

    [Fact]
    public void HoverSurvivesDataUpdateAndClearsOnInsufficientData()
    {
        var row = new UsageWindowRowViewModel(Window);
        row.Burndown = UsageBurndown.Build(Window, Samples, Now);
        row.HoverBurndown(1);
        Assert.Contains("40%", row.CaptionText);
        row.Burndown = UsageBurndown.Build(Window, [.. Samples, new(Now.AddMinutes(30), .7)], Now.AddMinutes(30));
        Assert.Contains("30%", row.CaptionText);
        row.Burndown = [];
        Assert.Equal(row.ResetText, row.CaptionText);
        row.Burndown = UsageBurndown.Build(Window, Samples, Now);
        Assert.Equal(row.ResetText, row.CaptionText);
    }

    [Fact]
    public void UnknownDurationOmitsIdealAndHoverRestoresLatestCaption()
    {
        var row = new UsageWindowRowViewModel(Window);
        row.Burndown = UsageBurndown.Build(Window with { Duration = null }, Samples, Now);
        Assert.All(row.Burndown, p => Assert.Null(p.IdealRemaining));
        row.HoverBurndown(.5);
        Assert.Contains("60%", row.CaptionText);
        row.ResetText = "new reset";
        Assert.Contains("60%", row.CaptionText);
        row.HoverBurndown(null);
        Assert.Equal("new reset", row.CaptionText);
        row.Burndown = [];
        row.HoverBurndown(.5);
        Assert.Equal("new reset", row.CaptionText);
    }
}
