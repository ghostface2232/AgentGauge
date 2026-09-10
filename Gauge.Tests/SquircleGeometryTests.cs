using Gauge.Views.Controls;

namespace Gauge.Tests;

/// <summary>
/// Corner math behind <see cref="SquircleBorder"/>. The control itself needs a XAML runtime;
/// the geometry does not, which is why the math lives apart from it.
/// </summary>
public sealed class SquircleGeometryTests
{
    [Fact]
    public void SmoothingOfZeroReproducesACircularCorner()
    {
        // 0.4477 x radius from the corner is where a quarter circle's Bezier handles sit, so a
        // smoothing of 0 must leave an ordinary rounded rectangle rather than a near-squircle.
        var (radius, handle) = SquircleGeometry.CornerMetrics(100, 40, 9, smoothing: 0);

        Assert.Equal(9, radius);
        Assert.Equal(9 * SquircleGeometry.CircularHandle, handle, 6);
    }

    [Fact]
    public void MoreSmoothingPullsHandlesTowardTheCornerWithoutCollapsingIt()
    {
        var circular = SquircleGeometry.CornerMetrics(100, 40, 9, smoothing: 0).Handle;
        var middle = SquircleGeometry.CornerMetrics(100, 40, 9, smoothing: 0.6).Handle;
        var most = SquircleGeometry.CornerMetrics(100, 40, 9, smoothing: 1).Handle;

        Assert.True(middle < circular);
        Assert.True(most < middle);
        // Handles at the corner itself would draw a visible point instead of a curve.
        Assert.True(most > 0);
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(4.2)]
    public void SmoothingIsClampedRatherThanExtrapolated(double smoothing)
    {
        // A caller's out-of-range value must not push the handles past the corner and invert
        // the curve.
        Assert.Equal(
            SquircleGeometry.CornerMetrics(100, 40, 9, 1.0).Handle,
            SquircleGeometry.CornerMetrics(100, 40, 9, smoothing).Handle,
            6);
    }

    [Fact]
    public void RadiusIsClampedToHalfTheShorterSide()
    {
        // Beyond half the shorter side the corners would overrun each other and fold the
        // outline back on itself.
        var (radius, _) = SquircleGeometry.CornerMetrics(100, 20, 40, smoothing: 0.6);

        Assert.Equal(10, radius);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(-5, 10)]
    [InlineData(10, -5)]
    public void DegenerateSizesProduceNoCornerInsteadOfNegativeMath(double width, double height)
    {
        var (radius, handle) = SquircleGeometry.CornerMetrics(width, height, 9, smoothing: 0.6);

        Assert.Equal(0, radius);
        Assert.Equal(0, handle);
    }

    [Fact]
    public void NegativeRadiusIsTreatedAsSquare()
    {
        var (radius, handle) = SquircleGeometry.CornerMetrics(100, 40, -9, smoothing: 0.6);

        Assert.Equal(0, radius);
        Assert.Equal(0, handle);
    }
}
