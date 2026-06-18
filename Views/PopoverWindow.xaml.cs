using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Foundation;
using Windows.Graphics;
using WinRT.Interop;

namespace Gauge.Views;

/// <summary>
/// Borderless, always-on-top popover modeled on the Windows 11 Quick Settings panel.
/// It is a separate <see cref="AppWindow"/> (not a WinUI Flyout): we drive show/hide,
/// positioning, light dismiss, the toggle-flicker guard, and the slide-in ourselves.
/// Content is a placeholder for now.
/// </summary>
public sealed partial class PopoverWindow : Window
{
    // --- Layout (device-independent pixels; scaled by DPI at placement time) ---
    private const double PopoverWidthDip = 360;
    private const double PopoverHeightDip = 480; // fallback height before content is measured
    private const double EdgeMarginDip = 12;
    private const double CornerRadiusDip = 8;
    // Reserve for footer + paddings when capping the scrollable body so the whole
    // popover stays within the work area on very tall content.
    private const double FooterChromeAllowanceDip = 96;

    // --- Slide-in animation (tunable) ---
    private const double SlideOffsetY = 24;       // start offset below final position
    private const int SlideDurationMs = 180;      // 150–200ms feels right

    // --- Toggle guard ---
    // A tray click that deactivates+hides the popover lands here first; any reopen
    // within this window is treated as a toggle-close and ignored.
    private const long ToggleGuardMs = 200;

    private readonly nint _hwnd;
    private bool _isShown;
    private long _lastHiddenAtTick;

    // Target monitor captured when the popover opens; reused for content-driven
    // resizes so the popover stays anchored even if the cursor later moves away.
    private RectInt32 _workArea;
    private double _scale = 1.0;

    // Guards against re-entrancy: MoveAndResize triggers a layout pass that fires
    // SizeChanged synchronously, which would call back into the resize logic.
    private bool _isResizing;
    private bool _isViewTransitioning;

    /// <summary>Raised whenever the popover is actually shown (after the toggle guard).</summary>
    public event EventHandler? Opened;

    public event EventHandler? SettingsOpened;

    public PopoverWindow()
    {
        InitializeComponent();

        _hwnd = WindowNative.GetWindowHandle(this);

        ConfigurePresenter();
        HideFromTaskbar();

        // Frosted Quick-Settings look.
        SystemBackdrop = new DesktopAcrylicBackdrop();

        // Start with DWM rounded corners. To switch to a larger radius later, make the
        // window background transparent and round RootBorder instead (its CornerRadius
        // is already set below); keep ApplyDwmRoundedCorners off in that mode.
        ApplyDwmRoundedCorners();
        RootBorder.CornerRadius = new CornerRadius(CornerRadiusDip);

        // Resize the window to match content height as it loads/changes (no filler).
        RootBorder.SizeChanged += OnContentSizeChanged;
        SettingsBorder.SizeChanged += OnContentSizeChanged;

        Activated += OnActivated;

        // Created hidden — this is a tray-only background app.
        AppWindow.Hide();
    }

    /// <summary>Tray left-click entry point: toggles the popover through the guard.</summary>
    public void Toggle()
    {
        // If the same click just deactivated+hid us, treat it as a toggle-close.
        if (Environment.TickCount64 - _lastHiddenAtTick < ToggleGuardMs)
        {
            return;
        }

        if (_isShown)
        {
            Hide();
        }
        else
        {
            Show();
        }
    }

    public void Show()
    {
        CaptureTargetMonitor();

        // Cap the scrollable body so a very tall popover still fits the work area.
        var maxBodyDip = (_workArea.Height / _scale) - (EdgeMarginDip * 2) - FooterChromeAllowanceDip;
        BodyScroll.MaxHeight = Math.Max(120, maxBodyDip);

        _isShown = true;

        // Size to content up front so we open at the right height (no flash, no
        // bottom filler); SizeChanged keeps it matched as data loads/changes.
        ResizeToContent();

        AppWindow.Show(activateWindow: true);
        Activate();
        // As a tray/background app, Activate() alone often does not make the window the
        // real foreground window, so DesktopAcrylic renders its inactive fallback (a
        // solid, near-white fill) until the user clicks it. Force foreground so the
        // acrylic engages immediately. Succeeds because the foreground-lock timeout was
        // zeroed at startup (see TrayIconService.DisableForegroundLock).
        _ = NativeMethods.SetForegroundWindow(_hwnd);
        RootHost.Focus(FocusState.Programmatic); // so ESC and light dismiss work
        PlayShowAnimation();

        // Confirmed open (passed the toggle guard) — let listeners (e.g. the
        // coordinator's debounced forced refresh) react.
        Opened?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Binds the popover content to a view model for data display.</summary>
    public void BindViewModel(object viewModel) => RootHost.DataContext = viewModel;

    /// <summary>Binds the settings view hosted inside this popover.</summary>
    public void BindSettingsViewModel(object viewModel) => SettingsBorder.DataContext = viewModel;

    public void Hide()
    {
        _isShown = false;
        _lastHiddenAtTick = Environment.TickCount64;
        AppWindow.Hide();
    }

    private void ConfigurePresenter()
    {
        var presenter = OverlappedPresenter.Create();
        presenter.SetBorderAndTitleBar(hasBorder: false, hasTitleBar: false);
        presenter.IsResizable = false;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        presenter.IsAlwaysOnTop = true;
        AppWindow.SetPresenter(presenter);

        // Hide from Alt-Tab.
        AppWindow.IsShownInSwitchers = false;
    }

    private void HideFromTaskbar()
    {
        // WS_EX_TOOLWINDOW keeps the popover off the taskbar (and reinforces Alt-Tab).
        var exStyle = NativeMethods.GetWindowLongPtr(_hwnd, NativeMethods.GWL_EXSTYLE);
        var updated = (nint)((long)exStyle | NativeMethods.WS_EX_TOOLWINDOW);
        _ = NativeMethods.SetWindowLongPtr(_hwnd, NativeMethods.GWL_EXSTYLE, updated);
    }

    private void ApplyDwmRoundedCorners()
    {
        var preference = NativeMethods.DWMWCP_ROUND;
        _ = NativeMethods.DwmSetWindowAttribute(
            _hwnd, NativeMethods.DWMWA_WINDOW_CORNER_PREFERENCE, ref preference, sizeof(int));
    }

    private void OnContentSizeChanged(object sender, SizeChangedEventArgs e)
    {
        // Keep the window height matched to the content as it loads/changes.
        if (_isShown && !_isViewTransitioning)
        {
            ResizeToContent();
        }
    }

    /// <summary>
    /// Resizes the window to the popover's intrinsic content height.
    /// We measure <see cref="RootBorder"/> with the fixed popover width and an
    /// UNBOUNDED height, so the result is the content's natural size and does not
    /// depend on the window's current size. (Reading ActualHeight here instead would
    /// feed the window's arranged — and therefore bounded — height back into the
    /// window size, collapsing it toward the minimum with each pass.)
    /// </summary>
    private void ResizeToContent()
    {
        if (_isResizing)
        {
            return;
        }

        _isResizing = true;
        try
        {
            var activeView = SettingsBorder.Visibility == Visibility.Visible ? SettingsBorder : RootBorder;
            activeView.Measure(new Size(PopoverWidthDip, double.PositiveInfinity));
            PositionAndResize(activeView.DesiredSize.Height);
        }
        finally
        {
            _isResizing = false;
        }
    }

    private void CaptureTargetMonitor()
    {
        // Anchor to the monitor under the cursor (where the tray click happened) so we
        // follow the user across monitors. WorkArea excludes the taskbar wherever it is.
        NativeMethods.GetCursorPos(out var cursor);
        var area = DisplayArea.GetFromPoint(new PointInt32(cursor.X, cursor.Y), DisplayAreaFallback.Nearest);
        _workArea = area.WorkArea;
        _scale = NativeMethods.GetDpiForWindow(_hwnd) / 96.0;
    }

    private void PositionAndResize(double contentHeightDip)
    {
        if (contentHeightDip <= 0)
        {
            contentHeightDip = PopoverHeightDip; // fallback before first layout
        }

        // WorkArea is physical pixels; convert DIP sizes with the captured DPI.
        var width = (int)Math.Round(PopoverWidthDip * _scale);
        var margin = (int)Math.Round(EdgeMarginDip * _scale);
        var maxHeight = _workArea.Height - (margin * 2);
        var height = Math.Min((int)Math.Round(contentHeightDip * _scale), maxHeight);

        var x = _workArea.X + _workArea.Width - width - margin;
        var y = _workArea.Y + _workArea.Height - height - margin;

        // Skip if unchanged to avoid relayout churn from SizeChanged.
        if (AppWindow.Size.Width == width && AppWindow.Size.Height == height
            && AppWindow.Position.X == x && AppWindow.Position.Y == y)
        {
            return;
        }

        AppWindow.MoveAndResize(new RectInt32(x, y, width, height));
    }

    private void PlayShowAnimation()
    {
        SlideTransform.Y = SlideOffsetY;
        RootHost.Opacity = 0;

        var duration = new Duration(TimeSpan.FromMilliseconds(SlideDurationMs));
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

        var slide = new DoubleAnimation
        {
            From = SlideOffsetY,
            To = 0,
            Duration = duration,
            EasingFunction = ease,
            EnableDependentAnimation = true,
        };
        Storyboard.SetTarget(slide, SlideTransform);
        Storyboard.SetTargetProperty(slide, "Y");

        var fade = new DoubleAnimation
        {
            From = 0,
            To = 1,
            Duration = duration,
            EasingFunction = ease,
        };
        Storyboard.SetTarget(fade, RootHost);
        Storyboard.SetTargetProperty(fade, "Opacity");

        var storyboard = new Storyboard();
        storyboard.Children.Add(slide);
        storyboard.Children.Add(fade);
        storyboard.Begin();
    }

    private void OnActivated(object sender, WindowActivatedEventArgs args)
    {
        // Manual light dismiss: hide when the window loses activation.
        if (args.WindowActivationState == WindowActivationState.Deactivated && _isShown)
        {
            Hide();
        }
    }

    private void OnEscapeInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        Hide();
    }

    private void OnSettingsClicked(object sender, RoutedEventArgs e)
    {
        if (_isViewTransitioning || SettingsBorder.Visibility == Visibility.Visible) return;
        AnimateViewTransition(
            RootBorder, SettingsBorder, UsageViewTransform, SettingsViewTransform, direction: 1);
        SettingsOpened?.Invoke(this, EventArgs.Empty);
    }

    private void OnSettingsBackClicked(object sender, RoutedEventArgs e)
    {
        if (_isViewTransitioning || RootBorder.Visibility == Visibility.Visible) return;
        AnimateViewTransition(
            SettingsBorder, RootBorder, SettingsViewTransform, UsageViewTransform, direction: -1);
    }

    private void AnimateViewTransition(
        FrameworkElement outgoing,
        FrameworkElement incoming,
        TranslateTransform outgoingTransform,
        TranslateTransform incomingTransform,
        int direction)
    {
        const double travel = 28;
        const int durationMs = 180;

        _isViewTransitioning = true;
        incoming.Visibility = Visibility.Visible;
        incoming.Opacity = 0;
        incomingTransform.X = travel * direction;
        outgoing.Opacity = 1;
        outgoingTransform.X = 0;

        // Hold the larger of both view heights during the overlap so neither sliding
        // surface is clipped; settle to the destination height after completion.
        incoming.Measure(new Size(PopoverWidthDip, double.PositiveInfinity));
        outgoing.Measure(new Size(PopoverWidthDip, double.PositiveInfinity));
        PositionAndResize(Math.Max(incoming.DesiredSize.Height, outgoing.DesiredSize.Height));

        var duration = new Duration(TimeSpan.FromMilliseconds(durationMs));
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var storyboard = new Storyboard();
        storyboard.Children.Add(CreateTransitionAnimation(
            outgoingTransform, "X", 0, -travel * direction, duration, ease, dependent: true));
        storyboard.Children.Add(CreateTransitionAnimation(
            outgoing, "Opacity", 1, 0, duration, ease));
        storyboard.Children.Add(CreateTransitionAnimation(
            incomingTransform, "X", travel * direction, 0, duration, ease, dependent: true));
        storyboard.Children.Add(CreateTransitionAnimation(
            incoming, "Opacity", 0, 1, duration, ease));
        storyboard.Completed += (_, _) =>
        {
            outgoing.Visibility = Visibility.Collapsed;
            outgoing.Opacity = 1;
            outgoingTransform.X = 0;
            incoming.Opacity = 1;
            incomingTransform.X = 0;
            _isViewTransitioning = false;
            ResizeToContent();
        };
        storyboard.Begin();
    }

    private static DoubleAnimation CreateTransitionAnimation(
        DependencyObject target,
        string property,
        double from,
        double to,
        Duration duration,
        EasingFunctionBase easing,
        bool dependent = false)
    {
        var animation = new DoubleAnimation
        {
            From = from,
            To = to,
            Duration = duration,
            EasingFunction = easing,
            EnableDependentAnimation = dependent,
        };
        Storyboard.SetTarget(animation, target);
        Storyboard.SetTargetProperty(animation, property);
        return animation;
    }

    private static class NativeMethods
    {
        public const int GWL_EXSTYLE = -20;
        public const long WS_EX_TOOLWINDOW = 0x00000080;
        public const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
        public const int DWMWCP_ROUND = 2;

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
        public static extern nint GetWindowLongPtr(nint hWnd, int nIndex);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
        public static extern nint SetWindowLongPtr(nint hWnd, int nIndex, nint dwNewLong);

        [DllImport("user32.dll")]
        public static extern uint GetDpiForWindow(nint hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetForegroundWindow(nint hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetCursorPos(out POINT lpPoint);

        [DllImport("dwmapi.dll")]
        public static extern int DwmSetWindowAttribute(nint hwnd, int attr, ref int attrValue, int attrSize);

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT
        {
            public int X;
            public int Y;
        }
    }
}
