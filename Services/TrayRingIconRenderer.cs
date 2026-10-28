using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Text.RegularExpressions;
using Gauge.Models;
using Gauge.ViewModels;

namespace Gauge.Services;

/// <summary>
/// Draws the tray icon for a single chosen tool: its logo inside a ring whose top arc is the
/// 5-hour window and whose bottom arc is the weekly (or billing-cycle) window. Both arcs fill
/// from the left. Rendered at the tray's exact pixel size, since at 16px every proportion is
/// a whole-pixel decision; the constants below were tuned on real-size mockups.
/// </summary>
internal sealed partial class TrayRingIconRenderer : IDisposable
{
    // Ring stroke as a share of the icon edge, floored so 16px still gets a visible line.
    private const float StrokeShare = 0.095f;
    private const float MinStroke = 1.25f;
    // The two arcs are split by gaps at 3 and 9 o'clock; small icons need a wider gap for
    // the split to survive anti-aliasing.
    private const float GapDegreesSmall = 34f;
    private const float GapDegrees = 28f;
    private const int SmallIconPx = 16;
    // Clearance between the ring's inner edge and the logo.
    private const float LogoPaddingShare = 0.06f;
    // Logo's larger dimension as a multiple of the clear inner radius.
    private const float LogoSpan = 1.7f;
    // Antigravity's arch has wide feet that read as touching the bottom arc when centered by
    // its bounding box; lift it by a share of the icon edge.
    private const float AntigravityLiftShare = 0.03f;

    // Taskbar-side colors. The tray draws outside XAML, so it cannot read the theme
    // dictionaries; these mirror the gauge ICO variants (white on dark, black on light) and
    // the popover's caution/danger hues.
    private static readonly Color DarkForeground = Color.White;
    private static readonly Color DarkTrack = Color.FromArgb(72, 255, 255, 255);
    private static readonly Color DarkCaution = Color.FromArgb(0xF7, 0xB5, 0x00);
    private static readonly Color DarkDanger = Color.FromArgb(0xFF, 0x5B, 0x4F);
    private static readonly Color LightForeground = Color.Black;
    private static readonly Color LightTrack = Color.FromArgb(56, 0, 0, 0);
    private static readonly Color LightCaution = Color.FromArgb(0xC2, 0x7C, 0x00);
    private static readonly Color LightDanger = Color.FromArgb(0xD1, 0x34, 0x38);

    private readonly Dictionary<ToolKind, (GraphicsPath Path, SizeF Size)?> _logos = new();

    public Icon Render(StatusRingState ring, int px, bool darkTaskbar, UsageDisplayBasis basis)
    {
        using var bitmap = new Bitmap(px, px, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.Clear(Color.Transparent);

            var foreground = darkTaskbar ? DarkForeground : LightForeground;
            var stroke = Math.Max(MinStroke, px * StrokeShare);
            var radius = px / 2f - stroke / 2;
            var center = px / 2f;
            var bounds = new RectangleF(center - radius, center - radius, radius * 2, radius * 2);
            var gap = px <= SmallIconPx ? GapDegreesSmall : GapDegrees;
            var span = 180f - gap;
            // Round caps smear into a blob at 16px; flat ends keep the arc tips crisp.
            var cap = px <= SmallIconPx ? LineCap.Flat : LineCap.Round;

            // GDI+ angles run clockwise from 3 o'clock. Top arc: from 9 o'clock over the top;
            // bottom arc: from 9 o'clock under the bottom, so both fill left to right.
            DrawArc(g, bounds, 180f + gap / 2, span, ring.Short, stroke, cap, foreground, darkTaskbar, basis);
            DrawArc(g, bounds, 180f - gap / 2, -span, ring.Long, stroke, cap, foreground, darkTaskbar, basis);

            var inner = radius - stroke / 2 - Math.Max(1f, px * LogoPaddingShare);
            if (Logo(ring.Kind) is { } logo && inner > 2)
            {
                var scale = inner * LogoSpan / Math.Max(logo.Size.Width, logo.Size.Height);
                var lift = ring.Kind == ToolKind.Antigravity ? px * AntigravityLiftShare : 0;
                using var matrix = new Matrix();
                matrix.Translate(center, center - lift);
                matrix.Scale(scale, scale);
                matrix.Translate(-logo.Size.Width / 2, -logo.Size.Height / 2);
                using var path = (GraphicsPath)logo.Path.Clone();
                path.Transform(matrix);
                using var brush = new SolidBrush(foreground);
                g.FillPath(brush, path);
            }
        }

        return ToIcon(bitmap);
    }

    private static void DrawArc(Graphics g, RectangleF bounds, float start, float sweep, UsageWindow? window,
        float stroke, LineCap cap, Color foreground, bool dark, UsageDisplayBasis basis)
    {
        using (var track = new Pen(dark ? DarkTrack : LightTrack, stroke) { StartCap = cap, EndCap = cap })
        {
            g.DrawArc(track, bounds, start, sweep);
        }

        if (window is null || !double.IsFinite(window.UsedRatio)) return;
        var used = Math.Clamp(window.UsedRatio, 0, 1);
        var fill = (float)(basis == UsageDisplayBasis.Remaining ? 1 - used : used);
        if (fill <= 0) return;

        // The level follows the used share in either basis, like the popover's bars.
        var color = UsageLevelClassifier.Classify(window.UsedRatio) switch
        {
            UsageLevel.Danger => dark ? DarkDanger : LightDanger,
            UsageLevel.Caution => dark ? DarkCaution : LightCaution,
            _ => foreground,
        };
        using var pen = new Pen(color, stroke) { StartCap = cap, EndCap = cap };
        g.DrawArc(pen, bounds, start, sweep * fill);
    }

    private (GraphicsPath Path, SizeF Size)? Logo(ToolKind kind)
    {
        if (_logos.TryGetValue(kind, out var cached)) return cached;
        var loaded = LoadLogo(kind);
        _logos[kind] = loaded;
        return loaded;
    }

    /// <summary>
    /// Reads <c>Assets\ToolLogos\{kind}.svg</c>: a single-path, single-color logo whose
    /// viewBox starts at the origin. A missing or unreadable file draws the ring alone.
    /// </summary>
    private static (GraphicsPath Path, SizeF Size)? LoadLogo(ToolKind kind)
    {
        var file = Path.Combine(AppContext.BaseDirectory, "Assets", "ToolLogos", $"{kind}.svg");
        try
        {
            if (!File.Exists(file)) return null;
            var svg = File.ReadAllText(file);
            var viewBox = ViewBoxPattern().Match(svg);
            var data = PathDataPattern().Match(svg);
            if (!viewBox.Success || !data.Success) throw new FormatException("No viewBox or path data.");
            var size = new SizeF(
                float.Parse(viewBox.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture),
                float.Parse(viewBox.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture));
            var path = new GraphicsPath(svg.Contains("evenodd", StringComparison.Ordinal) ? FillMode.Alternate : FillMode.Winding);
            foreach (var figure in SvgPathData.Parse(data.Groups[1].Value))
            {
                path.StartFigure();
                var from = figure.Start;
                foreach (var (c1, c2, end) in figure.Segments)
                {
                    path.AddBezier(from, c1, c2, end);
                    from = end;
                }
                if (figure.Closed) path.CloseFigure();
            }
            return (path, size);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException)
        {
            DiagnosticsLog.Write("tray", $"Tool logo for {kind} could not be read: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Wraps the bitmap as a one-image PNG ICO. <see cref="Bitmap.GetHicon"/> would leave an
    /// HICON the caller must destroy by hand; an Icon read from a stream owns its handle, so
    /// the tray service's usual dispose-on-swap releases it.
    /// </summary>
    private static Icon ToIcon(Bitmap bitmap)
    {
        using var png = new MemoryStream();
        bitmap.Save(png, ImageFormat.Png);
        using var ico = new MemoryStream();
        using (var writer = new BinaryWriter(ico, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            writer.Write((short)0);       // reserved
            writer.Write((short)1);       // type: icon
            writer.Write((short)1);       // image count
            writer.Write((byte)(bitmap.Width >= 256 ? 0 : bitmap.Width));
            writer.Write((byte)(bitmap.Height >= 256 ? 0 : bitmap.Height));
            writer.Write((byte)0);        // palette size
            writer.Write((byte)0);        // reserved
            writer.Write((short)1);       // color planes
            writer.Write((short)32);      // bits per pixel
            writer.Write((int)png.Length);
            writer.Write(6 + 16);         // image data offset
            writer.Write(png.ToArray());
        }
        ico.Position = 0;
        return new Icon(ico, bitmap.Width, bitmap.Height);
    }

    [GeneratedRegex("viewBox=\"0 0 ([\\d.]+) ([\\d.]+)\"")]
    private static partial Regex ViewBoxPattern();

    [GeneratedRegex(" d=\"([^\"]+)\"")]
    private static partial Regex PathDataPattern();

    public void Dispose()
    {
        foreach (var logo in _logos.Values) logo?.Path.Dispose();
        _logos.Clear();
    }
}
