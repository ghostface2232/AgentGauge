namespace Gauge.Views.Controls;

/// <summary>
/// Corner math for <see cref="SquircleBorder"/>, kept apart from the control so it can be
/// reasoned about (and tested) without a XAML runtime.
/// </summary>
internal static class SquircleGeometry
{
    /// <summary>
    /// Where a cubic Bézier's control points sit for a *circular* quarter corner, measured
    /// from the corner itself as a fraction of the radius. The familiar arc constant is
    /// 0.5523 from the tangent points; from the corner that leaves 1 - 0.5523.
    /// </summary>
    public const double CircularHandle = 0.4477;

    /// <summary>
    /// How far a smoothing of 1 may pull the handles in toward the corner.
    /// Pulling them all the way (factor 1) collapses the corner to a visible point, so the
    /// range stops at half — the resulting continuous-curvature corner is the squircle.
    /// </summary>
    private const double MaxSmoothingPull = 0.5;

    /// <summary>
    /// Resolves the corner radius actually drawable inside <paramref name="width"/> ×
    /// <paramref name="height"/> and the matching Bézier handle offset (measured from the
    /// corner along each edge). A radius larger than half the shorter side has no room, so
    /// it is clamped rather than allowed to fold the outline back on itself.
    /// </summary>
    public static (double Radius, double Handle) CornerMetrics(
        double width, double height, double radius, double smoothing)
    {
        var room = Math.Max(Math.Min(width, height) / 2.0, 0.0);
        var resolved = Math.Clamp(radius, 0.0, room);
        var pull = Math.Clamp(smoothing, 0.0, 1.0) * MaxSmoothingPull;
        return (resolved, resolved * CircularHandle * (1.0 - pull));
    }
}
