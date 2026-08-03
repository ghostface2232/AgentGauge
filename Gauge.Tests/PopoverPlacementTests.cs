using Gauge.Views;

namespace Gauge.Tests;

/// <summary>
/// Pure placement math for the popover and the tray menu across DPI scales and work-area
/// shapes. Closes the "Multi-monitor positioning" row from COVERAGE.md.
/// </summary>
public sealed class PopoverPlacementTests
{
    private static PopoverPlacement.Rect BottomRight(
        PopoverPlacement.Rect workArea, double scale, double contentHeightDip)
        => PopoverPlacement.BottomRight(
            workArea, scale, contentHeightDip,
            widthDip: 360, maxHeightDip: 800, edgeMarginDip: 12,
            fallbackContentHeightDip: 420, contentSafetyDip: 4);

    [Fact]
    public void AnchorsBottomRightAt100Percent()
    {
        var rect = BottomRight(new PopoverPlacement.Rect(0, 0, 1920, 1040), 1.0, contentHeightDip: 400);

        Assert.Equal(360, rect.Width);
        Assert.Equal(404, rect.Height); // content + safety
        Assert.Equal(1920 - 360 - 12, rect.X);
        Assert.Equal(1040 - 404 - 12, rect.Y);
    }

    [Fact]
    public void ScalesPhysicalPixelsAt150Percent()
    {
        var rect = BottomRight(new PopoverPlacement.Rect(0, 0, 3840, 2100), 1.5, contentHeightDip: 400);

        Assert.Equal(540, rect.Width); // 360 × 1.5
        Assert.Equal(606, rect.Height); // ceil(404 × 1.5)
        Assert.Equal(3840 - 540 - 18, rect.X);
        Assert.Equal(2100 - 606 - 18, rect.Y);
    }

    [Fact]
    public void CapsHeightToWorkAreaOnAShortMonitor()
    {
        // Work area shorter than the DIP maximum: height must be workArea − 2×margin.
        var rect = BottomRight(new PopoverPlacement.Rect(0, 0, 1280, 700), 1.0, contentHeightDip: 2000);
        Assert.Equal(700 - 24, rect.Height);
        Assert.Equal(700 - rect.Height - 12, rect.Y);
    }

    [Fact]
    public void CapsHeightToDipMaximumOnATallMonitor()
    {
        var rect = BottomRight(new PopoverPlacement.Rect(0, 0, 2160, 3740), 1.0, contentHeightDip: 5000);
        Assert.Equal(800, rect.Height);
    }

    [Fact]
    public void SecondaryMonitorOffsetIsRespected()
    {
        // A monitor left of and above the primary has negative origin coordinates.
        var rect = BottomRight(new PopoverPlacement.Rect(-1920, -400, 1920, 1040), 1.0, contentHeightDip: 400);
        Assert.Equal(-1920 + 1920 - 360 - 12, rect.X);
        Assert.Equal(-400 + 1040 - 404 - 12, rect.Y);
    }

    [Fact]
    public void ZeroContentHeightUsesFallback()
    {
        var rect = BottomRight(new PopoverPlacement.Rect(0, 0, 1920, 1040), 1.0, contentHeightDip: 0);
        Assert.Equal(424, rect.Height); // fallback 420 + safety 4
    }

    [Fact]
    public void MenuCentersOnCursorAboveTaskbar()
    {
        var (left, top) = PopoverPlacement.MenuAboveTray(
            workLeft: 0, workRight: 1920, workBottom: 1040,
            menuWidth: 200, menuHeight: 120, cursorX: 960, bottomMargin: 8, sideMargin: 8);

        Assert.Equal(960 - 100, left);
        Assert.Equal(1040 - 8 - 120, top);
    }

    [Fact]
    public void MenuClampsInsideWorkAreaEdges()
    {
        var (nearRight, _) = PopoverPlacement.MenuAboveTray(0, 1920, 1040, 200, 120, cursorX: 1900, bottomMargin: 8, sideMargin: 8);
        Assert.Equal(1920 - 8 - 200, nearRight);

        var (nearLeft, _) = PopoverPlacement.MenuAboveTray(0, 1920, 1040, 200, 120, cursorX: 10, bottomMargin: 8, sideMargin: 8);
        Assert.Equal(8, nearLeft);
    }
}
