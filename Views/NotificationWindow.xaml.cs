using System.Runtime.InteropServices;
using Gauge.Models;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Graphics;
using WinRT.Interop;

namespace Gauge.Views;

/// <summary>A non-activating, auto-dismiss usage notification above the work area.</summary>
public sealed partial class NotificationWindow : Window, IDisposable
{
    private const double WidthDip = 360;
    private const double HeightDip = 76;
    private const double EdgeMarginDip = 12;
    private const double StackGapDip = 8;
    private const double SlideOffsetDip = 20;
    private const int ShowDurationMs = 180;
    private const int VisibleDurationMs = 4500;
    // Exit slides the whole window (acrylic backdrop included) down off the work
    // area while fading the content. The backdrop lives at the window/compositor
    // level, not in the XAML tree, so a content-only opacity fade cannot dissolve
    // it — moving the window is what makes the frosted panel actually disappear.
    // Exit fades the whole window — content and acrylic backdrop together — via a
    // layered-window alpha ramp. Moving the window instead made the DWM backdrop
    // trail the content by a frame (text appeared to lead the frosted panel);
    // fading the window as one surface keeps them locked together.
    private const int DismissFadeDurationMs = 320;

    private readonly nint _hwnd;
    private readonly NativeMethods.SUBCLASSPROC _subclassProc;
    private readonly DispatcherTimer _dismissTimer = new() { Interval = TimeSpan.FromMilliseconds(VisibleDurationMs) };
    private readonly AlwaysActiveAcrylicBackdrop _backdrop = new();
    private Storyboard? _storyboard;
    private UsageNotification? _notification;
    private int _iconLoadId;
    private string? _iconKey;
    private TimeSpan _dismissStartTime;
    private bool _dismissClockStarted;
    private bool _isDismissing;
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

        SystemBackdrop = _backdrop;
        RootHost.ActualThemeChanged += (_, _) =>
        {
            _backdrop.SetTheme(RootHost.ActualTheme);
            UpdateDwmTheme();
            if (_isVisible) UpdateIcon();
        };
        _dismissTimer.Tick += (_, _) => BeginDismiss();
        ApplyDwmAttributes();
        AppWindow.Hide();
    }

    public void Show(
        UsageNotification notification,
        ElementTheme? themeOverride = null,
        TimeSpan? visibleDuration = null)
    {
        _dismissTimer.Stop();
        _storyboard?.Stop();
        if (_isDismissing)
        {
            CompositionTarget.Rendering -= OnDismissRendering;
            ClearLayered();
            _isDismissing = false;
        }

        RootHost.RequestedTheme = themeOverride ?? ElementTheme.Default;
        _backdrop.SetTheme(RootHost.ActualTheme);
        _dismissTimer.Interval = visibleDuration ?? TimeSpan.FromMilliseconds(VisibleDurationMs);
        _notification = notification;
        TitleText.Text = notification.Title;
        MessageText.Text = notification.Message;
        UpdateIcon();
        PositionAboveWorkArea(HeightDip);

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
        if (!_isVisible || _isDismissing) return;
        _isDismissing = true;
        _dismissTimer.Stop();
        _storyboard?.Stop();
        _storyboard = null;
        _dismissClockStarted = false;
        // Turn the window layered (starting fully opaque) so the whole surface —
        // XAML content and acrylic backdrop alike — can fade as a single unit.
        SetLayeredAlpha(255);
        CompositionTarget.Rendering += OnDismissRendering;
    }

    private void OnDismissRendering(object? sender, object args)
    {
        var time = ((RenderingEventArgs)args).RenderingTime;
        if (!_dismissClockStarted)
        {
            _dismissClockStarted = true;
            _dismissStartTime = time;
        }

        var duration = TimeSpan.FromMilliseconds(DismissFadeDurationMs);
        var progress = Math.Clamp((time - _dismissStartTime) / duration, 0, 1);
        var fade = progress * progress * (3 - 2 * progress); // smoothstep ease in-out
        SetLayeredAlpha((byte)Math.Clamp((int)Math.Round(255 * (1 - fade)), 0, 255));

        if (progress < 1) return;

        CompositionTarget.Rendering -= OnDismissRendering;
        AppWindow.Hide();
        // Drop the layered style so the next Show() renders the acrylic at full
        // fidelity, and reset the content opacity the entrance animates from.
        ClearLayered();
        RootHost.Opacity = 0;
        _isDismissing = false;
        _isVisible = false;
        Dismissed?.Invoke(this, EventArgs.Empty);
    }

    private void SetLayeredAlpha(byte alpha)
    {
        var exStyle = (long)NativeMethods.GetWindowLongPtr(_hwnd, NativeMethods.GWL_EXSTYLE);
        if ((exStyle & NativeMethods.WS_EX_LAYERED) == 0)
        {
            _ = NativeMethods.SetWindowLongPtr(
                _hwnd, NativeMethods.GWL_EXSTYLE, (nint)(exStyle | NativeMethods.WS_EX_LAYERED));
        }
        _ = NativeMethods.SetLayeredWindowAttributes(_hwnd, 0, alpha, NativeMethods.LWA_ALPHA);
    }

    private void ClearLayered()
    {
        var exStyle = (long)NativeMethods.GetWindowLongPtr(_hwnd, NativeMethods.GWL_EXSTYLE);
        if ((exStyle & NativeMethods.WS_EX_LAYERED) != 0)
        {
            _ = NativeMethods.SetWindowLongPtr(
                _hwnd, NativeMethods.GWL_EXSTYLE, (nint)(exStyle & ~NativeMethods.WS_EX_LAYERED));
        }
    }

    private async void UpdateIcon()
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
        // Decode to the icon's exact on-screen pixel size (see IconDecoder for why a plain
        // BitmapImage shimmers). _iconLoadId discards a load a newer Show()/theme change has
        // superseded mid-await; _iconKey skips redundant reloads of the same variant/scale.
        var scale = NotificationIcon.XamlRoot?.RasterizationScale
            ?? (NativeMethods.GetDpiForWindow(_hwnd) / 96.0);
        if (scale <= 0) scale = 1.0;
        var targetPx = (uint)Math.Max(1, Math.Round(NotificationIcon.Width * scale));

        var key = $"{stem}{suffix}@{targetPx}";
        if (key == _iconKey) return;

        var path = Path.Combine(AppContext.BaseDirectory, "Assets", $"{stem}{suffix}.ico");
        var loadId = ++_iconLoadId;
        var source = await IconDecoder.LoadScaledAsync(path, targetPx);
        if (source is null || loadId != _iconLoadId) return;
        NotificationIcon.Source = source;
        _iconKey = key;
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
        if (_isDismissing) CompositionTarget.Rendering -= OnDismissRendering;
        _ = NativeMethods.RemoveWindowSubclass(_hwnd, _subclassProc, 2);
        Close();
    }

    private static class NativeMethods
    {
        public const int GWL_EXSTYLE = -20, GWL_STYLE = -16;
        public const long WS_EX_TOOLWINDOW = 0x80, WS_EX_LAYERED = 0x00080000, WS_CAPTION = 0x00C00000;
        public const uint LWA_ALPHA = 0x2;
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
        [DllImport("user32.dll")] public static extern bool SetLayeredWindowAttributes(nint hwnd, uint crKey, byte alpha, uint flags);
        [DllImport("comctl32.dll")] public static extern bool SetWindowSubclass(nint hwnd, SUBCLASSPROC proc, nuint id, nuint data);
        [DllImport("comctl32.dll")] public static extern bool RemoveWindowSubclass(nint hwnd, SUBCLASSPROC proc, nuint id);
        [DllImport("comctl32.dll")] public static extern nint DefSubclassProc(nint hwnd, uint message, nint wParam, nint lParam);
        [DllImport("dwmapi.dll")] public static extern int DwmSetWindowAttribute(nint hwnd, int attr, ref int value, int size);
    }
}
