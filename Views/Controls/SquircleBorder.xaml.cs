using Windows.Foundation;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;

namespace Gauge.Views.Controls;

/// <summary>
/// A <see cref="Border"/>-shaped container drawn as a squircle: a rounded rectangle whose
/// corners use continuous curvature instead of a quarter circle, so each corner blends into
/// the straight edge rather than meeting it at an abrupt change in curvature.
///
/// WinUI's <c>CornerRadius</c> can only produce circular corners, so the outline is drawn as
/// a Path instead. Each corner is one cubic Bezier with its control points pulled in toward
/// the corner (see <see cref="SquircleGeometry"/>) — a cheap, close stand-in for a true
/// superellipse that holds up at the small sizes this is used at.
///
/// The control sizes to its <see cref="Child"/> plus <see cref="Control.Padding"/> and the
/// outline is rebuilt on every size change, so content of any width works.
/// </summary>
[ContentProperty(Name = nameof(Child))]
public sealed partial class SquircleBorder : UserControl
{
    public SquircleBorder()
    {
        InitializeComponent();
    }

    public static readonly DependencyProperty ChildProperty = DependencyProperty.Register(
        nameof(Child), typeof(UIElement), typeof(SquircleBorder), new PropertyMetadata(null));

    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(
        nameof(Fill), typeof(Brush), typeof(SquircleBorder), new PropertyMetadata(null));

    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
        nameof(Stroke), typeof(Brush), typeof(SquircleBorder), new PropertyMetadata(null));

    public static readonly DependencyProperty StrokeThicknessProperty = DependencyProperty.Register(
        nameof(StrokeThickness), typeof(double), typeof(SquircleBorder),
        new PropertyMetadata(0.0, OnShapeChanged));

    public static readonly DependencyProperty RadiusProperty = DependencyProperty.Register(
        nameof(Radius), typeof(double), typeof(SquircleBorder),
        new PropertyMetadata(8.0, OnShapeChanged));

    public static readonly DependencyProperty SmoothingProperty = DependencyProperty.Register(
        nameof(Smoothing), typeof(double), typeof(SquircleBorder),
        new PropertyMetadata(0.6, OnShapeChanged));

    /// <summary>The content drawn inside the shape, inset by <see cref="Control.Padding"/>.</summary>
    public UIElement? Child
    {
        get => (UIElement?)GetValue(ChildProperty);
        set => SetValue(ChildProperty, value);
    }

    /// <summary>Interior brush of the shape.</summary>
    public Brush? Fill
    {
        get => (Brush?)GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    /// <summary>Outline brush; null draws no outline.</summary>
    public Brush? Stroke
    {
        get => (Brush?)GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    /// <summary>Outline width. The outline is inset so it stays inside the control's bounds.</summary>
    public double StrokeThickness
    {
        get => (double)GetValue(StrokeThicknessProperty);
        set => SetValue(StrokeThicknessProperty, value);
    }

    /// <summary>
    /// Corner radius, clamped to half the shorter side. A squircle wants a noticeably larger
    /// value than the equivalent rounded rectangle, because the squared corner takes some of
    /// the visual roundness back.
    /// </summary>
    public double Radius
    {
        get => (double)GetValue(RadiusProperty);
        set => SetValue(RadiusProperty, value);
    }

    /// <summary>
    /// How far the corner moves from a circular arc (0) toward a fully squared continuous
    /// corner (1). The default is the Apple-like middle ground.
    /// </summary>
    public double Smoothing
    {
        get => (double)GetValue(SmoothingProperty);
        set => SetValue(SmoothingProperty, value);
    }

    private static void OnShapeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((SquircleBorder)d).RebuildShape();

    private void OnSizeChanged(object sender, SizeChangedEventArgs e) => RebuildShape();

    private void RebuildShape()
    {
        // A stroke straddles the path, so half of it would fall outside the control and be
        // clipped by the parent. Inset the outline by that half instead.
        var inset = StrokeThickness / 2.0;
        var width = ActualWidth - StrokeThickness;
        var height = ActualHeight - StrokeThickness;
        if (width <= 0 || height <= 0)
        {
            ShapePath.Data = null;
            return;
        }

        var (radius, handle) = SquircleGeometry.CornerMetrics(width, height, Radius, Smoothing);
        var left = inset;
        var top = inset;
        var right = inset + width;
        var bottom = inset + height;

        var figure = new PathFigure
        {
            StartPoint = new Point(left + radius, top),
            IsClosed = true,
            IsFilled = true,
        };

        // Clockwise from where the top-left corner rejoins the top edge: edge, corner, edge,
        // corner, … Each corner's control points sit `handle` from the corner along the two
        // edges it joins; that distance is what shapes the curve (circular at 0.4477 x the
        // radius, progressively squarer below it).
        AddLine(figure, right - radius, top);
        AddCorner(figure, right - handle, top, right, top + handle, right, top + radius);
        AddLine(figure, right, bottom - radius);
        AddCorner(figure, right, bottom - handle, right - handle, bottom, right - radius, bottom);
        AddLine(figure, left + radius, bottom);
        AddCorner(figure, left + handle, bottom, left, bottom - handle, left, bottom - radius);
        AddLine(figure, left, top + radius);
        AddCorner(figure, left, top + handle, left + handle, top, left + radius, top);

        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        ShapePath.Data = geometry;
    }

    private static void AddLine(PathFigure figure, double x, double y)
        => figure.Segments.Add(new LineSegment { Point = new Point(x, y) });

    private static void AddCorner(
        PathFigure figure,
        double control1X, double control1Y,
        double control2X, double control2Y,
        double endX, double endY)
        => figure.Segments.Add(new BezierSegment
        {
            Point1 = new Point(control1X, control1Y),
            Point2 = new Point(control2X, control2Y),
            Point3 = new Point(endX, endY),
        });
}
