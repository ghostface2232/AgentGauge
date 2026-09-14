using Windows.Foundation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Gauge.Views.Controls;

/// <summary>
/// A circular usage gauge: a 270° track that opens at the bottom, with a colored fill arc
/// whose length is the <see cref="Percent"/> (0–100). Only the ring is drawn here; the
/// caller stacks the number/percent text over the center. The fill color comes from
/// <see cref="FillBrush"/> (the caller binds it to the usage level), and the geometry is
/// rebuilt whenever the control is resized or a property changes.
/// </summary>
public sealed partial class UsageGauge : UserControl
{
    // Opening centered at the bottom: a 90° gap there leaves a 270° sweep that starts at
    // the lower-left (135°) and runs clockwise — through the top — to the lower-right.
    // Angles are degrees measured clockwise from the positive X axis with Y pointing down,
    // so 90° is straight down (the bottom) and 270° is straight up (the top).
    private const double StartAngleDegrees = 135.0;
    private const double TotalSweepDegrees = 270.0;
    private const double StrokeThickness = 7.0;

    public UsageGauge()
    {
        InitializeComponent();
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new UsageGaugeAutomationPeer(this);

    private sealed class UsageGaugeAutomationPeer(UsageGauge owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override string GetClassNameCore() => nameof(UsageGauge);
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Text;
        // The composite name already includes every value; paths have no independent meaning.
        protected override IList<AutomationPeer> GetChildrenCore() => new List<AutomationPeer>();
    }

    public static readonly DependencyProperty PercentProperty = DependencyProperty.Register(
        nameof(Percent), typeof(double), typeof(UsageGauge),
        new PropertyMetadata(0.0, OnVisualChanged));

    public static readonly DependencyProperty FillBrushProperty = DependencyProperty.Register(
        nameof(FillBrush), typeof(Brush), typeof(UsageGauge),
        new PropertyMetadata(null, OnFillBrushChanged));

    /// <summary>The fraction filled, 0–100.</summary>
    public double Percent
    {
        get => (double)GetValue(PercentProperty);
        set => SetValue(PercentProperty, value);
    }

    /// <summary>Stroke color of the fill arc (the caller maps the usage level to a brush).</summary>
    public Brush? FillBrush
    {
        get => (Brush?)GetValue(FillBrushProperty);
        set => SetValue(FillBrushProperty, value);
    }

    private static void OnVisualChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((UsageGauge)d).RebuildArcs();

    private static void OnFillBrushChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((UsageGauge)d).FillPath.Stroke = (Brush?)e.NewValue;

    private void OnSizeChanged(object sender, SizeChangedEventArgs e) => RebuildArcs();

    private void RebuildArcs()
    {
        var width = ActualWidth;
        var height = ActualHeight;
        if (width <= 0 || height <= 0)
        {
            return;
        }

        var centerX = width / 2;
        var centerY = height / 2;
        // Inset by half the stroke (plus 1px breathing room) so the rounded ends stay
        // inside the bounds rather than clipping at the edges.
        var radius = Math.Min(width, height) / 2 - StrokeThickness / 2 - 1;
        if (radius <= 0)
        {
            return;
        }

        TrackPath.Data = BuildArc(centerX, centerY, radius, StartAngleDegrees, TotalSweepDegrees);

        var percent = Math.Clamp(Percent, 0.0, 100.0);
        // Below ~half a percent the rounded cap alone is the whole mark; skip the arc so a
        // true zero shows an empty track rather than a stray dot.
        FillPath.Data = percent <= 0.5
            ? null
            : BuildArc(centerX, centerY, radius, StartAngleDegrees, TotalSweepDegrees * percent / 100.0);
    }

    private static PathGeometry BuildArc(double centerX, double centerY, double radius, double startAngle, double sweep)
    {
        var figure = new PathFigure
        {
            StartPoint = PointOnCircle(centerX, centerY, radius, startAngle),
            IsClosed = false,
        };
        figure.Segments.Add(new ArcSegment
        {
            Point = PointOnCircle(centerX, centerY, radius, startAngle + sweep),
            Size = new Size(radius, radius),
            SweepDirection = SweepDirection.Clockwise,
            IsLargeArc = sweep > 180.0,
        });

        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        return geometry;
    }

    private static Point PointOnCircle(double centerX, double centerY, double radius, double angleDegrees)
    {
        var radians = angleDegrees * Math.PI / 180.0;
        return new Point(centerX + radius * Math.Cos(radians), centerY + radius * Math.Sin(radians));
    }
}
