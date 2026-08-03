namespace Gauge.Views;

/// <summary>
/// Pure placement math for the popover window and the tray context menu, extracted from
/// the WinUI/Win32 handlers so multi-monitor / DPI cases are unit-testable. All inputs
/// and outputs are physical pixels except the DIP sizes, which are converted with the
/// caller-captured scale.
/// </summary>
public static class PopoverPlacement
{
    public readonly record struct Rect(int X, int Y, int Width, int Height);

    /// <summary>
    /// Bottom-right popover placement inside a monitor's work area (which already
    /// excludes the taskbar wherever it is docked). Height tracks the measured content
    /// plus a safety allowance, capped by both the work area and the DIP maximum.
    /// </summary>
    public static Rect BottomRight(
        Rect workArea,
        double scale,
        double contentHeightDip,
        double widthDip,
        double maxHeightDip,
        double edgeMarginDip,
        double fallbackContentHeightDip,
        double contentSafetyDip)
    {
        if (contentHeightDip <= 0)
        {
            contentHeightDip = fallbackContentHeightDip; // fallback before first layout
        }

        var width = (int)Math.Round(widthDip * scale);
        var margin = (int)Math.Round(edgeMarginDip * scale);
        var maxHeight = Math.Min(
            workArea.Height - (margin * 2),
            (int)Math.Round(maxHeightDip * scale));
        var height = Math.Min(
            (int)Math.Ceiling((contentHeightDip + contentSafetyDip) * scale),
            maxHeight);

        var x = workArea.X + workArea.Width - width - margin;
        var y = workArea.Y + workArea.Height - height - margin;
        return new Rect(x, y, width, height);
    }

    /// <summary>
    /// Tray context-menu placement: bottom-anchored above the taskbar, horizontally
    /// centered on the cursor but clamped inside the work area's side margins.
    /// </summary>
    public static (int Left, int Top) MenuAboveTray(
        int workLeft, int workRight, int workBottom,
        int menuWidth, int menuHeight,
        int cursorX, int bottomMargin, int sideMargin)
    {
        var top = workBottom - bottomMargin - menuHeight;
        var left = cursorX - (menuWidth / 2);
        if (left + menuWidth > workRight - sideMargin)
        {
            left = workRight - sideMargin - menuWidth;
        }
        if (left < workLeft + sideMargin)
        {
            left = workLeft + sideMargin;
        }
        return (left, top);
    }
}
