using Gauge.Models;
using Gauge.ViewModels;

namespace Gauge.Tests;

/// <summary>
/// The absolute-counts caption (shown only when the provider reported both numbers) and the
/// display basis: the remaining basis inverts the shown percent — full at zero usage — while
/// the level color keeps following the used share.
/// </summary>
public sealed class UsageWindowRowViewModelTests
{
    [Fact]
    public void UsedBasisShowsUsedShareByDefault()
    {
        var row = new UsageWindowRowViewModel(Window());

        Assert.Equal(UsageDisplayBasis.Used, row.DisplayBasis);
        Assert.Equal(42.0, row.Percent, precision: 6);
        Assert.Equal("42%", row.PercentText);
        Assert.Equal("42", row.PercentNumber);
    }

    [Fact]
    public void RemainingBasisInvertsShownPercent()
    {
        var row = new UsageWindowRowViewModel(Window(), UsageDisplayBasis.Remaining);

        Assert.Equal(58.0, row.Percent, precision: 6);
        Assert.Equal("58%", row.PercentText);
        Assert.Equal("58", row.PercentNumber);
    }

    [Fact]
    public void RemainingBasisShowsFullBarForUntouchedWindow()
    {
        var row = new UsageWindowRowViewModel(Window() with { UsedRatio = 0 }, UsageDisplayBasis.Remaining);

        Assert.Equal(100.0, row.Percent);
        Assert.Equal("100%", row.PercentText);
        Assert.Equal(UsageLevel.Ok, row.Level);
    }

    [Fact]
    public void RemainingBasisFloorsAtZeroForOverLimitWindow()
    {
        // Used keeps the raw over-limit reading; remaining never goes negative.
        var used = new UsageWindowRowViewModel(Window() with { UsedRatio = 1.04 });
        var remaining = new UsageWindowRowViewModel(Window() with { UsedRatio = 1.04 }, UsageDisplayBasis.Remaining);

        Assert.Equal("104%", used.PercentText);
        Assert.Equal(100.0, used.Percent);
        Assert.Equal("0%", remaining.PercentText);
        Assert.Equal(0.0, remaining.Percent);
    }

    [Theory]
    [InlineData(0.995, "100%", "0%")]
    [InlineData(0.005, "1%", "99%")]
    [InlineData(0.425, "43%", "57%")]
    [InlineData(0.5, "50%", "50%")]
    public void UsedAndRemainingTextsAlwaysSumToOneHundred(double usedRatio, string used, string remaining)
    {
        // Rounding the complement independently would read 100% / 1% at 99.5% used; the
        // remaining number is derived from the rounded used number instead.
        Assert.Equal(used, new UsageWindowRowViewModel(Window() with { UsedRatio = usedRatio }).PercentText);
        Assert.Equal(remaining,
            new UsageWindowRowViewModel(Window() with { UsedRatio = usedRatio }, UsageDisplayBasis.Remaining).PercentText);
    }

    [Fact]
    public void RemainingBasisCapsAtOneHundredForNegativeUsedRatio()
    {
        // A misbehaving provider reporting below zero must not read as more than a full quota.
        var remaining = new UsageWindowRowViewModel(Window() with { UsedRatio = -0.05 }, UsageDisplayBasis.Remaining);

        Assert.Equal("100%", remaining.PercentText);
        Assert.Equal("100", remaining.PercentNumber);
        Assert.Equal(100.0, remaining.Percent);
    }

    [Fact]
    public void SwitchingBasisRaisesChangeForEveryPercentProperty()
    {
        // The bar width, caption and gauge number are separate bindings; each must be notified.
        var row = new UsageWindowRowViewModel(Window());
        var updates = new List<string?>();
        row.PropertyChanged += (_, e) => updates.Add(e.PropertyName);

        row.DisplayBasis = UsageDisplayBasis.Remaining;

        Assert.Contains(nameof(row.Percent), updates);
        Assert.Contains(nameof(row.PercentText), updates);
        Assert.Contains(nameof(row.PercentNumber), updates);
    }

    [Fact]
    public void SwitchingBasisRederivesPercentWithoutNewData()
    {
        var row = new UsageWindowRowViewModel(Window() with { UsedRatio = 0.95 });
        Assert.Equal("95%", row.PercentText);

        row.DisplayBasis = UsageDisplayBasis.Remaining;

        Assert.Equal(5.0, row.Percent, precision: 6);
        Assert.Equal("5%", row.PercentText);
        Assert.Equal("5", row.PercentNumber);
        // Color still tracks consumption: a nearly drained window stays in danger.
        Assert.Equal(UsageLevel.Danger, row.Level);

        row.DisplayBasis = UsageDisplayBasis.Used;

        Assert.Equal("95%", row.PercentText);
    }

    [Fact]
    public void UpdateKeepsCurrentBasis()
    {
        var row = new UsageWindowRowViewModel(Window(), UsageDisplayBasis.Remaining);

        row.Update(Window() with { UsedRatio = 0.25 });

        Assert.Equal("75%", row.PercentText);
        Assert.Equal(UsageLevel.Ok, row.Level);
    }

    [Fact]
    public void CountsCaptionFormatsBothNumbers()
    {
        var row = new UsageWindowRowViewModel(Window() with { UsedTokens = 1280, LimitTokens = 3000 });

        Assert.Equal("1,280 / 3,000", row.CountsText);
        Assert.True(row.HasCounts);
    }

    [Fact]
    public void CountsCaptionHiddenWithoutBothNumbers()
    {
        Assert.False(new UsageWindowRowViewModel(Window()).HasCounts);
        Assert.False(new UsageWindowRowViewModel(Window() with { UsedTokens = 10 }).HasCounts);
        Assert.False(new UsageWindowRowViewModel(Window() with { LimitTokens = 300 }).HasCounts);
    }

    [Fact]
    public void SparklineIsVisibleOnlyWhenEnabledAndDataSuffices()
    {
        var now = new DateTimeOffset(2026, 9, 14, 12, 0, 0, TimeSpan.Zero);
        var window = Window() with { Type = UsageWindowType.FiveHour, UsedRatio = .6,
            Duration = TimeSpan.FromHours(5), ResetTime = now.AddHours(2) };
        var row = new UsageWindowRowViewModel(window);

        // Shown by default, but there is nothing to draw until enough samples arrive.
        Assert.True(row.ShowSparkline);
        Assert.False(row.IsSparklineVisible);

        row.Burndown = UsageBurndown.Build(window,
            [new(now.AddHours(-2), .2), new(now.AddHours(-1), .4), new(now, .6)], now);
        Assert.True(row.HasBurndown);
        Assert.True(row.IsSparklineVisible);

        // Turning the preference off hides the sparkline but keeps the data for re-enabling.
        row.ShowSparkline = false;
        Assert.False(row.IsSparklineVisible);
        Assert.True(row.HasBurndown);

        row.ShowSparkline = true;
        Assert.True(row.IsSparklineVisible);
    }

    private static UsageWindow Window() => new()
    {
        Type = UsageWindowType.BillingCycle,
        Label = "월간",
        UsedRatio = 0.42,
    };
}
