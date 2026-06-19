using System.Runtime.InteropServices;
using Gauge.Models;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics;
using WinRT.Interop;

namespace Gauge.Views;

/// <summary>A non-activating, auto-dismiss usage notification above the work area.</summary>
public sealed partial class NotificationWindow : Window, IDisposable
{
    private const double WidthDip = 360;
    private const double HeightDip = 112;
    private const double EdgeMarginDip = 12;
    private const double StackGapDip = 8;
    private const double SlideOffsetDip = 20;
    private const int ShowDurationMs = 180;
    private const int VisibleDurationMs = 4500;
    private const int FadeDurationMs = 260;

    private readonly nint _hwnd;
    private readonly NativeMethods.SUBCLASSPROC _subclassProc;
    private readonly DispatcherTimer _dismissTimer = new() { Interval = TimeSpan.FromMilliseconds(VisibleDurationMs) };
    private Storyboard? _storyboard;
    private UsageNotification? _notification;
    private bool _isVisible;
    private bool _disposed;

    public event EventHandler? Dismissed;

    public NotificationWindow()
    {
        InitializeComponent();
        _hwnd = WindowNative.GetWindowHandle(this);
        _subclassProc = NonClientSubclassProc;
        _ = NativeMethods.SetWindowSubclass(_hwnd, _subclassProc, 2, 0);

        var presenter = OverlappedPresenter.Create();
        presenter.SetBorderAndTitleBar(false, false);
        presenter.IsResizable = false;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        presenter.IsAlwaysOnTop = true;
        AppWindow.SetPresenter(presenter);
        AppWindow.IsShownInSwitchers = false;

        var exStyle = NativeMethods.GetWindowLongPtr(_hwnd, NativeMethods.GWL_EXSTYLE);
        _ = NativeMethods.SetWindowLongPtr(
            _hwnd, NativeMethods.GWL_EXSTYLE, (nint)((long)exStyle | NativeMethods.WS_EX_TOOLWINDOW));
        RemoveCaptionFrame();

        SystemBackdrop = new DesktopAcrylicBackdrop();
        RootHost.ActualThemeChanged += (_, _) =>
        {
            UpdateDwmTheme();
            if (_isVisible) UpdateIcon();
        };
        _dismissTimer.Tick += (_, _) => BeginDismiss();
        ApplyDwmAttributes();
        AppWindow.Hide();
    }

    public void Show(
        UsageNotification notification,
        int suppressedCount = 0,
        ElementTheme? themeOverride = null,
        TimeSpan? visibleDuration = null)
    {
        _dismissTimer.Stop();
        _storyboard?.Stop();

        RootHost.RequestedTheme = themeOverride ?? ElementTheme.Default;
        _dismissTimer.Interval = visibleDuration ?? TimeSpan.FromMilliseconds(VisibleDurationMs);
        _notification = notification;
        TitleText.Text = notification.Title;
        MessageText.Text = notification.Message;
        SuppressedCountText.Text = suppressedCount > 1
            ? $"방해 금지 중 발생한 {suppressedCount}건 중 최근 알림"
            : string.Empty;
        SuppressedCountText.Visibility = suppressedCount > 1
            ? Visibility.Visible
            : Visibility.Collapsed;
        AccentBar.Background = (Brush)Application.Current.Resources[notification.Level switch
        {
            UsageLevel.Danger => "UsageDangerBrush",
            UsageLevel.Caution => "UsageCautionBrush",
            _ => "UsageOkBrush",
        }];
        UpdateIcon();
        PositionAboveWorkArea(suppressedCount > 1 ? HeightDip + 16 : HeightDip);

        _isVisible = true;
        AppWindow.Show(activateWindow: false);
        RemoveCaptionFrame();
        ApplyDwmAttributes();
        PlayEntrance();
        _dismissTimer.Start();
    }

    private void PositionAboveWorkArea(double heightDip)
    {
        var area = DisplayArea.Primary.WorkArea;
        var scale = NativeMethods.GetDpiForWindow(_hwnd) / 96.0;
        var width = (int)Math.Round(WidthDip * scale);
        var height = (int)Math.Round(heightDip * scale);
        var margin = (int)Math.Round(EdgeMarginDip * scale);
        var gap = (int)Math.Round(StackGapDip * scale);
        var x = area.X + area.Width - width - margin;
        var y = area.Y + area.Height - height - margin - gap;
        AppWindow.MoveAndResize(new RectInt32(x, y, width, height));
    }

    private void PlayEntrance()
    {
        RootHost.Opacity = 0;
        SlideTransform.Y = SlideOffsetDip;
        var duration = new Duration(TimeSpan.FromMilliseconds(ShowDurationMs));
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        _storyboard = new Storyboard();
        _storyboard.Children.Add(CreateAnimation(RootHost, "Opacity", 0, 1, duration, ease));
        _storyboard.Children.Add(CreateAnimation(SlideTransform, "Y", SlideOffsetDip, 0, duration, ease, true));
        _storyboard.Begin();
    }

    private void BeginDismiss()
    {
        if (!_isVisible) return;
        _dismissTimer.Stop();
        _storyboard?.Stop();
        var duration = new Duration(TimeSpan.FromMilliseconds(FadeDurationMs));
        var ease = new CubicEase { EasingMode = EasingMode.EaseIn };
        var storyboard = new Storyboard();
        storyboard.Children.Add(CreateAnimation(RootHost, "Opacity", RootHost.Opacity, 0, duration, ease));
        storyboard.Completed += (_, _) =>
        {
            if (!ReferenceEquals(_storyboard, storyboard)) return;
            AppWindow.Hide();
            _isVisible = false;
            Dismissed?.Invoke(this, EventArgs.Empty);
        };
        _storyboard = storyboard;
        storyboard.Begin();
    }

    private void UpdateIcon()
    {
        if (_notification is not { } notification) return;
        var stem = RootHost.ActualTheme == ElementTheme.Dark ? "gauge_icon_dark" : "gauge_icon";
        var suffix = notification.Kind == UsageNotificationKind.Reset
            ? "_reset"
            : notification.Level switch
        {
            UsageLevel.Danger => "_90",
            UsageLevel.Caution => "_70",
            _ => string.Empty,
        };
        var path = Path.Combine(AppContext.BaseDirectory, "Assets", $"{stem}{suffix}.ico");
        NotificationIcon.Source = new BitmapImage(new Uri(path));
    }

    private void RemoveCaptionFrame()
    {
        var style = (long)NativeMethods.GetWindowLongPtr(_hwnd, NativeMethods.GWL_STYLE);
        _ = NativeMethods.SetWindowLongPtr(_hwnd, NativeMethods.GWL_STYLE, (nint)(style & ~NativeMethods.WS_CAPTION));
        _ = NativeMethods.SetWindowPos(_hwnd, nint.Zero, 0, 0, 0, 0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER
            | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_FRAMECHANGED);
    }

    private void ApplyDwmAttributes()
    {
        var corner = NativeMethods.DWMWCP_ROUND;
        _ = NativeMethods.DwmSetWindowAttribute(_hwnd, NativeMethods.DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));
        UpdateDwmTheme();
    }

    private void UpdateDwmTheme()
    {
        var dark = RootHost.ActualTheme == ElementTheme.Dark ? 1 : 0;
        _ = NativeMethods.DwmSetWindowAttribute(_hwnd, NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
        var border = NativeMethods.DWMWA_COLOR_NONE;
        _ = NativeMethods.DwmSetWindowAttribute(_hwnd, NativeMethods.DWMWA_BORDER_COLOR, ref border, sizeof(int));
    }

    private nint NonClientSubclassProc(nint hwnd, uint message, nint wParam, nint lParam, nuint id, nuint data)
        => message == NativeMethods.WM_NCCALCSIZE && wParam != 0
            ? 0
            : NativeMethods.DefSubclassProc(hwnd, message, wParam, lParam);

    private static DoubleAnimation CreateAnimation(
        DependencyObject target, string property, double from, double to,
        Duration duration, EasingFunctionBase easing, bool dependent = false)
    {
        var animation = new DoubleAnimation
        {
            From = from, To = to, Duration = duration,
            EasingFunction = easing, EnableDependentAnimation = dependent,
        };
        Storyboard.SetTarget(animation, target);
        Storyboard.SetTargetProperty(animation, property);
        return animation;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _dismissTimer.Stop();
        _storyboard?.Stop();
        _ = NativeMethods.RemoveWindowSubclass(_hwnd, _subclassProc, 2);
        Close();
    }

    private static class NativeMethods
    {
        public const int GWL_EXSTYLE = -20, GWL_STYLE = -16;
        public const long WS_EX_TOOLWINDOW = 0x80, WS_CAPTION = 0x00C00000;
        public const uint WM_NCCALCSIZE = 0x0083;
        public const uint SWP_NOSIZE = 1, SWP_NOMOVE = 2, SWP_NOZORDER = 4, SWP_NOACTIVATE = 0x10, SWP_FRAMECHANGED = 0x20;
        public const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20, DWMWA_WINDOW_CORNER_PREFERENCE = 33, DWMWA_BORDER_COLOR = 34;
        public const int DWMWCP_ROUND = 2, DWMWA_COLOR_NONE = unchecked((int)0xFFFFFFFE);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        public delegate nint SUBCLASSPROC(nint hwnd, uint message, nint wParam, nint lParam, nuint id, nuint data);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] public static extern nint GetWindowLongPtr(nint hwnd, int index);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] public static extern nint SetWindowLongPtr(nint hwnd, int index, nint value);
        [DllImport("user32.dll")] public static extern uint GetDpiForWindow(nint hwnd);
        [DllImport("user32.dll")] public static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int cx, int cy, uint flags);
        [DllImport("comctl32.dll")] public static extern bool SetWindowSubclass(nint hwnd, SUBCLASSPROC proc, nuint id, nuint data);
        [DllImport("comctl32.dll")] public static extern bool RemoveWindowSubclass(nint hwnd, SUBCLASSPROC proc, nuint id);
        [DllImport("comctl32.dll")] public static extern nint DefSubclassProc(nint hwnd, uint message, nint wParam, nint lParam);
        [DllImport("dwmapi.dll")] public static extern int DwmSetWindowAttribute(nint hwnd, int attr, ref int value, int size);
    }
}
