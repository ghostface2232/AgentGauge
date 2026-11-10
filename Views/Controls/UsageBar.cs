using Gauge.Models;
using Gauge.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace Gauge.Views.Controls;

/// <summary>The quota meter with optional daily shares inside the existing track.</summary>
public sealed class UsageBar : UserControl
{
    private const double TrackHeight = 8;
    private const double SegmentGap = 2;
    private readonly Canvas _canvas = new();
    private readonly ToolTip _tooltip = new();
    private int _selected = -1;

    public UsageBar()
    {
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Stretch;
        Content = _canvas;
        SizeChanged += (_, _) => Redraw();
        PointerMoved += OnPointerMoved;
        PointerExited += (_, _) => SelectSegment(-1);
        GotFocus += (_, _) => SelectSegment(0);
        LostFocus += (_, _) => SelectSegment(-1);
        Unloaded += (_, _) => SelectSegment(-1);
        DataContextChanged += (_, _) => UpdateDescription();
        ToolTipService.SetToolTip(this, _tooltip);
    }

    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(UsageBar), new PropertyMetadata(0d, Changed));
    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }

    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(
        nameof(Fill), typeof(Brush), typeof(UsageBar), new PropertyMetadata(null, Changed));
    public Brush? Fill { get => (Brush?)GetValue(FillProperty); set => SetValue(FillProperty, value); }

    public static readonly DependencyProperty TrackProperty = DependencyProperty.Register(
        nameof(Track), typeof(Brush), typeof(UsageBar), new PropertyMetadata(null, Changed));
    public Brush? Track { get => (Brush?)GetValue(TrackProperty); set => SetValue(TrackProperty, value); }

    public static readonly DependencyProperty BasisProperty = DependencyProperty.Register(
        nameof(Basis), typeof(UsageDisplayBasis), typeof(UsageBar), new PropertyMetadata(UsageDisplayBasis.Used, Changed));
    public UsageDisplayBasis Basis { get => (UsageDisplayBasis)GetValue(BasisProperty); set => SetValue(BasisProperty, value); }

    public static readonly DependencyProperty SegmentsProperty = DependencyProperty.Register(
        nameof(Segments), typeof(IReadOnlyList<DailyBarSegment>), typeof(UsageBar), new PropertyMetadata(null, Changed));
    public IReadOnlyList<DailyBarSegment>? Segments
    {
        get => (IReadOnlyList<DailyBarSegment>?)GetValue(SegmentsProperty);
        set => SetValue(SegmentsProperty, value);
    }

    private static void Changed(DependencyObject sender, DependencyPropertyChangedEventArgs args)
        => ((UsageBar)sender).Redraw();

    protected override AutomationPeer OnCreateAutomationPeer() => new UsageBarAutomationPeer(this);

    private sealed class UsageBarAutomationPeer(UsageBar owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override string GetClassNameCore() => nameof(UsageBar);
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Text;
    }

    private void Redraw()
    {
        _canvas.Children.Clear();
        _canvas.Background = Background;
        var value = double.IsFinite(Value) ? Math.Clamp(Value / 100, 0, 1) : 0;
        AddTrack(0, ActualWidth, Track, 1, new CornerRadius(4));
        var segmented = Segments is { Count: > 0 };
        IsTabStop = segmented;
        AutomationProperties.SetAccessibilityView(this, segmented ? AccessibilityView.Content : AccessibilityView.Raw);
        if (!segmented || Basis == UsageDisplayBasis.Remaining)
            AddTrack(0, ActualWidth * value, Fill, 1, new CornerRadius(4));
        if (segmented)
        {
            var unattributedWidth = Basis == UsageDisplayBasis.Used
                ? ActualWidth * UsageDailyBreakdown.UnattributedUsedRatio(value, Segments!.Sum(s => s.UsedRatio))
                : 0;
            var unattributedGap = Math.Min(SegmentGap / 2, unattributedWidth / 4);
            var x = Basis == UsageDisplayBasis.Remaining ? value * ActualWidth : 0;
            for (var i = 0; i < Segments!.Count; i++)
            {
                var segment = Segments[i];
                var width = segment.UsedRatio * ActualWidth;
                var gap = Math.Min(SegmentGap, width / 4);
                var leading = i == 0 ? 0 : gap / 2;
                var last = i == Segments.Count - 1;
                var trailing = last ? Math.Min(gap / 2, unattributedGap) : gap / 2;
                var opacity = i % 2 == 0 ? 1 : 0.72;
                if (Basis == UsageDisplayBasis.Remaining) opacity *= 0.5;
                AddTrack(x + leading, width - leading - trailing, Fill, opacity,
                    new CornerRadius(i == 0 ? 4 : 2, last && unattributedGap < 0.01 ? 4 : 2,
                        last && unattributedGap < 0.01 ? 4 : 2, i == 0 ? 4 : 2));
                x += width;
            }
            // The provider's total still determines the fill. Only measured dates get hit targets.
            AddTrack(x + unattributedGap, unattributedWidth - unattributedGap, Fill, 0.5, new CornerRadius(2, 4, 4, 2));
        }
        if (_selected >= (Segments?.Count ?? 0)) SelectSegment(-1);
        UpdateDescription();
    }

    private void AddTrack(double x, double width, Brush? brush, double opacity, CornerRadius radius)
    {
        if (width <= 0) return;
        var border = new Border
        {
            Width = width, Height = TrackHeight, Background = brush, Opacity = opacity,
            CornerRadius = radius, IsHitTestVisible = false,
        };
        Canvas.SetLeft(border, x);
        Canvas.SetTop(border, Math.Max(0, (ActualHeight - TrackHeight) / 2));
        _canvas.Children.Add(border);
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs args)
    {
        if (Segments is not { Count: > 0 } segments || ActualWidth <= 0) return;
        var position = args.GetCurrentPoint(this).Position.X / ActualWidth;
        var start = Basis == UsageDisplayBasis.Remaining ? Value / 100 : 0;
        for (var i = 0; i < segments.Count; i++)
        {
            var end = start + segments[i].UsedRatio;
            if (position >= start && position <= end) { SelectSegment(i); return; }
            start = end;
        }
        SelectSegment(-1);
    }

    protected override void OnKeyDown(KeyRoutedEventArgs args)
    {
        if (Segments is not { Count: > 0 } segments) { base.OnKeyDown(args); return; }
        var index = args.Key switch
        {
            VirtualKey.Left => Math.Max(0, _selected - 1),
            VirtualKey.Right => Math.Min(segments.Count - 1, _selected + 1),
            VirtualKey.Home => 0,
            VirtualKey.End => segments.Count - 1,
            _ => -1,
        };
        if (index >= 0) { SelectSegment(index); args.Handled = true; }
        else base.OnKeyDown(args);
    }

    private void SelectSegment(int index)
    {
        _selected = index;
        UpdateDescription();
    }

    private void UpdateDescription()
    {
        var segment = Segments is { } segments && _selected >= 0 && _selected < segments.Count ? segments[_selected] : null;
        var description = segment?.Description;
        (DataContext as UsageWindowRowViewModel)?.HoverDailySegment(description);
        _tooltip.Content = description;
        AutomationProperties.SetName(this, description ?? string.Join(". ",
            Segments?.Select(item => item.Description) ?? []));
    }
}
