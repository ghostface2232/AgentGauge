using System.Drawing;
using Gauge.Models;
using Gauge.Services;

namespace Gauge.Tests;

public sealed class StatusRingTests
{
    private static UsageWindow Window(UsageWindowType type, double used, string? group = null, string? id = null) => new()
    {
        Type = type,
        UsedRatio = used,
        Label = type.ToString(),
        GroupLabel = group,
        Id = id,
    };

    [Fact]
    public void AccountWideWindowsWinOverScopedOnes()
    {
        var (shortWindow, longWindow) = StatusRingSelection.Select([
            Window(UsageWindowType.Weekly, 0.95, group: "Fable", id: "fable"),
            Window(UsageWindowType.FiveHour, 0.40),
            Window(UsageWindowType.Weekly, 0.20),
        ]);

        Assert.Equal(0.40, shortWindow?.UsedRatio);
        Assert.Equal(0.20, longWindow?.UsedRatio);
    }

    [Fact]
    public void FamilyScopedToolUsesItsFirstFamily()
    {
        var (shortWindow, longWindow) = StatusRingSelection.Select([
            Window(UsageWindowType.FiveHour, 0.10, group: "Gemini", id: "g5"),
            Window(UsageWindowType.Weekly, 0.30, group: "Gemini", id: "gw"),
            Window(UsageWindowType.FiveHour, 0.80, group: "Claude/GPT", id: "c5"),
            Window(UsageWindowType.Weekly, 0.60, group: "Gemini 4 Argon", id: "aw"),
        ]);

        Assert.Equal("g5", shortWindow?.Id);
        Assert.Equal("gw", longWindow?.Id);
    }

    [Fact]
    public void WeeklyOnlyLeavesTheShortArcEmpty()
    {
        var (shortWindow, longWindow) = StatusRingSelection.Select([Window(UsageWindowType.Weekly, 0.48)]);

        Assert.Null(shortWindow);
        Assert.Equal(0.48, longWindow?.UsedRatio);
    }

    [Fact]
    public void BillingCycleToolShowsItsMostUsedQuotaOnTheLongArc()
    {
        var (shortWindow, longWindow) = StatusRingSelection.Select([
            Window(UsageWindowType.BillingCycle, 0.10, id: "chat"),
            Window(UsageWindowType.BillingCycle, 0.65, id: "premium_interactions"),
        ]);

        Assert.Null(shortWindow);
        Assert.Equal("premium_interactions", longWindow?.Id);
    }

    [Fact]
    public void NoWindowsSelectsNothing()
        => Assert.Equal((null, null), StatusRingSelection.Select([]));

    [Fact]
    public void PathDataReadsAbsoluteAndRelativeCommands()
    {
        var figures = SvgPathData.Parse("M10 10H20V20L10 20Z m5-5 c1 1 2 2 3 3 s4 4 5 5 q1 1 2-.5");

        Assert.Equal(2, figures.Count);
        Assert.True(figures[0].Closed);
        Assert.Equal(new PointF(10, 10), figures[0].Start);
        // Z closes without a segment of its own; the drawing path closes the figure.
        Assert.Equal(3, figures[0].Segments.Count);
        Assert.Equal(new PointF(10, 20), figures[0].Segments[^1].End);
        // The relative moveto starts from the closed figure's start point.
        Assert.Equal(new PointF(15, 5), figures[1].Start);
        Assert.Equal(new PointF(18, 8), figures[1].Segments[0].End);
        Assert.Equal(new PointF(23, 13), figures[1].Segments[1].End);
        // "2-.5" is two numbers: the sign starts a new one.
        Assert.Equal(new PointF(25, 12.5f), figures[1].Segments[2].End);
        Assert.False(figures[1].Closed);
    }

    [Theory]
    [InlineData("10 10")]
    [InlineData("M0 0 A5 5 0 0 1 10 10")]
    [InlineData("M0 0 Z 5 5")]
    [InlineData("M0 0 L5")]
    public void MalformedPathDataIsRejected(string data)
        => Assert.Throws<FormatException>(() => SvgPathData.Parse(data));

    [Theory]
    [InlineData(16)]
    [InlineData(24)]
    public void RendersAnIconAtTheTrayPixelSize(int px)
    {
        using var renderer = new TrayRingIconRenderer();
        var ring = new StatusRingState(ToolKind.Codex, "Codex",
            Window(UsageWindowType.FiveHour, 0.95), Window(UsageWindowType.Weekly, 0.40));

        using var icon = renderer.Render(ring, px, darkTaskbar: true, UsageDisplayBasis.Used);
        using var bitmap = icon.ToBitmap();

        Assert.Equal(px, bitmap.Width);
        Assert.Equal(px, bitmap.Height);
        // The top arc is filled in the danger color near 12 o'clock.
        var top = bitmap.GetPixel(px / 2, 0);
        Assert.True(top.A > 0 && top.R > top.G, $"Expected a red-ish top arc pixel, got {top}.");
    }
}
