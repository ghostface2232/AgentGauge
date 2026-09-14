using Gauge.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace Gauge.Views.Controls;

public sealed partial class UsageSparkline : UserControl
{
    private const double Inset = 1;
    public UsageSparkline() => InitializeComponent();
    public static readonly DependencyProperty PointsProperty = DependencyProperty.Register(
        nameof(Points), typeof(IReadOnlyList<BurndownPoint>), typeof(UsageSparkline),
        new PropertyMetadata(null, (d, _) => ((UsageSparkline)d).Redraw()));
    public IReadOnlyList<BurndownPoint>? Points
    {
        get => (IReadOnlyList<BurndownPoint>?)GetValue(PointsProperty);
        set => SetValue(PointsProperty, value);
    }
    private void OnSizeChanged(object sender, SizeChangedEventArgs e) => Redraw();
    private void Redraw()
    {
        if (Actual is null) return;
        var actual = new PointCollection();
        var ideal = new PointCollection();
        if (Points is { Count: >= UsageBurndown.MinimumSamples } points)
            foreach (var p in points)
            {
                var x = Inset + p.X * Math.Max(0, ActualWidth - 2 * Inset);
                actual.Add(new Point(x, Inset + (1 - p.Remaining) * Math.Max(0, ActualHeight - 2 * Inset)));
                if (p.IdealRemaining is { } remaining)
                    ideal.Add(new Point(x, Inset + (1 - remaining) * Math.Max(0, ActualHeight - 2 * Inset)));
            }
        Actual.Points = actual;
        Ideal.Points = ideal;
    }
    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (DataContext is UsageWindowRowViewModel row && ActualWidth > 2 * Inset)
            row.HoverBurndown((e.GetCurrentPoint(this).Position.X - Inset) / (ActualWidth - 2 * Inset));
    }
    private void OnPointerExited(object sender, PointerRoutedEventArgs e) => ClearHover();
    private void OnUnloaded(object sender, RoutedEventArgs e) => ClearHover();
    private void ClearHover()
    {
        if (DataContext is UsageWindowRowViewModel row) row.HoverBurndown(null);
    }
}
