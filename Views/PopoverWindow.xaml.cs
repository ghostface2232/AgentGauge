using System.Runtime.InteropServices;
using Gauge.ViewModels;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
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
/// It hosts two views — the usage cards and the settings panel — with an animated
/// transition between them.
/// </summary>
public sealed partial class PopoverWindow : Window
{
    // --- Layout (device-independent pixels; scaled by DPI at placement time) ---
    private const double PopoverWidthDip = 360;
    private const double PopoverHeightDip = 480; // fallback height before content is measured
    // Hard cap on the popover's height. Content taller than this scrolls inside
    // BodyScroll instead of growing the window; the footer bar stays pinned. ~800
    // keeps the popover comfortable even on a 1080p display.
    private const double MaxPopoverHeightDip = 800;
    private const double EdgeMarginDip = 12;
    private const double CornerRadiusDip = 8;
    // DesiredSize can land on a fractional physical pixel at 125/150% DPI. Keep a
    // small client-area inset so the final footer row never touches the window edge.
    private const double ContentHeightSafetyDip = 4;
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
    private readonly NativeMethods.SUBCLASSPROC _windowSubclassProc;
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
    private bool _isSettingsView;
    private double _usageViewHeightDip;
    private bool _usageLayoutRefreshPending;
    private Storyboard? _viewTransitionStoryboard;

    /// <summary>Raised whenever the popover is actually shown (after the toggle guard).</summary>
    public event EventHandler? Opened;

    public event EventHandler? SettingsOpened;

    public PopoverWindow()
    {
        InitializeComponent();

        _hwnd = WindowNative.GetWindowHandle(this);
        _windowSubclassProc = NonClientSubclassProc;
        _ = NativeMethods.SetWindowSubclass(
            _hwnd, _windowSubclassProc, NativeMethods.NonClientSubclassId, 0);
        Closed += (_, _) =>
        {
            _ = NativeMethods.RemoveWindowSubclass(
                _hwnd, _windowSubclassProc, NativeMethods.NonClientSubclassId);
        };

        ConfigurePresenter();
        HideFromTaskbar();
        RemoveCaptionFrame();

        // Frosted Quick-Settings look.
        SystemBackdrop = new DesktopAcrylicBackdrop();
        RootHost.ActualThemeChanged += (_, _) => UpdateDwmTheme();

        // Start with DWM rounded corners. To switch to a larger radius later, make the
        // window background transparent and round RootBorder instead (its CornerRadius
        // is already set below); keep ApplyDwmRoundedCorners off in that mode.
        ApplyDwmRoundedCorners();
        UpdateDwmTheme();
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

        // Cap the scrollable body so the window fits the work area AND never exceeds
        // MaxPopoverHeightDip. The footer bar (FooterChromeAllowanceDip) stays pinned
        // below; taller content scrolls within BodyScroll.
        var maxWindowDip = Math.Min((_workArea.Height / _scale) - (EdgeMarginDip * 2), MaxPopoverHeightDip);
        var maxBodyDip = maxWindowDip - FooterChromeAllowanceDip;
        BodyScroll.MaxHeight = Math.Max(120, maxBodyDip);

        _isShown = true;

        // Size to content up front so we open at the right height (no flash, no
        // bottom filler); SizeChanged keeps it matched as data loads/changes.
        ResizeToContent();

        AppWindow.Show(activateWindow: true);
        // Apply after showing as well: DWM can recreate non-client attributes when
        // an AppWindow transitions from hidden to visible. This includes the corner
        // preference — if it is not re-applied, the window reverts to a square (the
        // borderless tool-window default) and its 1px client edge shows as a square
        // light seam that ignores the XAML border's rounded corners.
        ApplyDwmRoundedCorners();
        UpdateDwmTheme();
        Activate();
        // As a tray/background app, Activate() alone often does not make the window the
        // real foreground window, so DesktopAcrylic renders its inactive fallback (a
        // solid, near-white fill) until the user clicks it. Force foreground so the
        // acrylic engages immediately. Succeeds because the foreground-lock timeout was
        // zeroed at startup (see TrayIconService.DisableForegroundLock).
        _ = NativeMethods.SetForegroundWindow(_hwnd);
        UpdateDwmTheme();
        // Re-apply once more after the show settles: some DWM attribute recreation
        // lands slightly after AppWindow.Show returns.
        RootHost.DispatcherQueue.TryEnqueue(() =>
        {
            RemoveCaptionFrame();
            ApplyDwmRoundedCorners();
            UpdateDwmTheme();
        });
        PlayShowAnimation();

        // Confirmed open (passed the toggle guard) — let listeners (e.g. the
        // coordinator's debounced forced refresh) react.
        Opened?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Binds the popover content to a view model for data display.</summary>
    public void BindViewModel(object viewModel) => RootHost.DataContext = viewModel;

    /// <summary>
    /// Re-measures the usage view after a completed coordinator update. The enqueue
    /// lets bindings and ItemsControl containers finish their layout first.
    /// </summary>
    public void RefreshUsageLayout()
    {
        _usageLayoutRefreshPending = true;
        RootHost.DispatcherQueue.TryEnqueue(() =>
        {
            if (!_isShown || _isViewTransitioning || RootBorder.Visibility != Visibility.Visible) return;
            MeasureAndStoreUsageHeight();
            PositionAndResize(_usageViewHeightDip);
        });
    }

    /// <summary>Binds the settings view hosted inside this popover.</summary>
    public void BindSettingsViewModel(object viewModel) => SettingsBorder.DataContext = viewModel;

    public void Hide()
    {
        _isShown = false;
        _lastHiddenAtTick = Environment.TickCount64;
        ResetToUsageView();
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

    /// <summary>
    /// Strips the residual WS_CAPTION (= WS_BORDER | WS_DLGFRAME) from the window.
    /// OverlappedPresenter.SetBorderAndTitleBar(false, false) does not reliably remove
    /// it at the Win32 level for a non-resizable borderless window (microsoft-ui-xaml
    /// #7629). The leftover frame's INNER edge is rectangular, so it shows as a thin
    /// light line with square corners sitting inside the DWM-rounded outer edge — the
    /// "two different corner radii" seam. DWMWCP_ROUND keeps the outer corners rounded
    /// without the caption frame.
    /// </summary>
    private void RemoveCaptionFrame()
    {
        var style = (long)NativeMethods.GetWindowLongPtr(_hwnd, NativeMethods.GWL_STYLE);
        var updated = style & ~NativeMethods.WS_CAPTION;
        if (updated == style)
        {
            return;
        }

        _ = NativeMethods.SetWindowLongPtr(_hwnd, NativeMethods.GWL_STYLE, (nint)updated);
        // The frame size is cached until SWP_FRAMECHANGED forces a non-client recompute.
        _ = NativeMethods.SetWindowPos(
            _hwnd, nint.Zero, 0, 0, 0, 0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER
                | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_FRAMECHANGED);
    }

    /// <summary>
    /// WinUI's non-resizable OverlappedPresenter can restore WS_DLGFRAME after we
    /// clear WS_CAPTION. Claim the complete window rectangle as client area so that
    /// residual style has no non-client pixels to paint. DWM still owns the shadow
    /// and rounded outer clip.
    /// </summary>
    private nint NonClientSubclassProc(
        nint hWnd,
        uint message,
        nint wParam,
        nint lParam,
        nuint subclassId,
        nuint referenceData)
    {
        if (message == NativeMethods.WM_NCCALCSIZE && wParam != 0)
        {
            return 0;
        }

        return NativeMethods.DefSubclassProc(hWnd, message, wParam, lParam);
    }

    private void ApplyDwmRoundedCorners()
    {
        var preference = NativeMethods.DWMWCP_ROUND;
        _ = NativeMethods.DwmSetWindowAttribute(
            _hwnd, NativeMethods.DWMWA_WINDOW_CORNER_PREFERENCE, ref preference, sizeof(int));
    }

    private void UpdateDwmTheme()
    {
        // Borderless windows can retain a light non-client frame/shadow even while
        // their XAML content is dark. Tell DWM the actual XAML theme so edge pixels
        // and the shadow use the matching dark/light treatment.
        var useDarkMode = RootHost.ActualTheme == ElementTheme.Dark ? 1 : 0;
        _ = NativeMethods.DwmSetWindowAttribute(
            _hwnd, NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE, ref useDarkMode, sizeof(int));

        // The full-window client area established by NonClientSubclassProc makes a
        // separate DWM outline unnecessary. Suppress it in both themes so Windows'
        // accent color cannot produce a blue ring around the rounded acrylic edge.
        var borderColor = NativeMethods.DWMWA_COLOR_NONE;
        _ = NativeMethods.DwmSetWindowAttribute(
            _hwnd, NativeMethods.DWMWA_BORDER_COLOR, ref borderColor, sizeof(int));
    }

    private void OnContentSizeChanged(object sender, SizeChangedEventArgs e)
    {
        // Keep the window height matched to the content as it loads/changes.
        if (_isShown && !_isViewTransitioning && RootBorder.Visibility == Visibility.Visible)
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
            if (SettingsBorder.Visibility == Visibility.Visible && _usageViewHeightDip > 0)
            {
                PositionAndResize(_usageViewHeightDip);
                return;
            }

            MeasureAndStoreUsageHeight();
            PositionAndResize(_usageViewHeightDip);
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
        var maxHeight = Math.Min(
            _workArea.Height - (margin * 2),
            (int)Math.Round(MaxPopoverHeightDip * _scale));
        var height = Math.Min(
            (int)Math.Ceiling((contentHeightDip + ContentHeightSafetyDip) * _scale),
            maxHeight);

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
        _viewTransitionStoryboard = storyboard;
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
        if (_isViewTransitioning || _isSettingsView) return;
        _isSettingsView = true;
        AnimateViewTransition(
            RootBorder, SettingsBorder, UsageViewTransform, SettingsViewTransform, direction: 1);
        SettingsOpened?.Invoke(this, EventArgs.Empty);
    }

    private void OnAddServiceClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement anchor
            || SettingsBorder.DataContext is not SettingsViewModel settings)
        {
            return;
        }

        // Build the picker from the registry's available tools each time it opens, so it
        // reflects what is still unregistered.
        var flyout = new MenuFlyout();
        var addable = settings.AddableTools;
        if (addable.Count == 0)
        {
            flyout.Items.Add(new MenuFlyoutItem { Text = "추가할 서비스가 없습니다", IsEnabled = false });
        }
        else
        {
            foreach (var tool in addable)
            {
                var kind = tool.Kind;
                var item = new MenuFlyoutItem { Text = tool.DisplayName };
                item.Click += (_, _) => settings.AddTool(kind);
                flyout.Items.Add(item);
            }
        }

        flyout.ShowAt(anchor);
    }

    private void OnSettingsBackClicked(object sender, RoutedEventArgs e)
    {
        if (_isViewTransitioning || !_isSettingsView) return;
        _isSettingsView = false;
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

        if (ReferenceEquals(incoming, RootBorder) && _usageLayoutRefreshPending)
        {
            MeasureAndStoreUsageHeight();
            PositionAndResize(_usageViewHeightDip);
        }

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
            if (!ReferenceEquals(_viewTransitionStoryboard, storyboard)) return;
            _viewTransitionStoryboard = null;
            outgoing.Visibility = Visibility.Collapsed;
            outgoing.Opacity = 1;
            outgoingTransform.X = 0;
            incoming.Opacity = 1;
            incomingTransform.X = 0;
            _isViewTransitioning = false;
        };
        // The completion handler deliberately ignores stale storyboards. Register this
        // one as the active transition before starting it; otherwise the identity check
        // always fails and _isViewTransitioning remains true forever, disabling Back.
        _viewTransitionStoryboard = storyboard;
        storyboard.Begin();
    }

    private void ResetToUsageView()
    {
        _viewTransitionStoryboard?.Stop();
        _viewTransitionStoryboard = null;
        _isViewTransitioning = false;
        _isSettingsView = false;

        RootBorder.Visibility = Visibility.Visible;
        RootBorder.Opacity = 1;
        UsageViewTransform.X = 0;
        SettingsBorder.Visibility = Visibility.Collapsed;
        SettingsBorder.Opacity = 1;
        SettingsViewTransform.X = 0;
    }

    private void MeasureAndStoreUsageHeight()
    {
        RootBorder.Measure(new Size(PopoverWidthDip, double.PositiveInfinity));
        _usageViewHeightDip = RootBorder.DesiredSize.Height;
        _usageLayoutRefreshPending = false;
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
        public const int GWL_STYLE = -16;
        public const long WS_EX_TOOLWINDOW = 0x00000080;
        // WS_CAPTION = WS_BORDER | WS_DLGFRAME — the non-client frame whose square
        // inner edge shows as the thin light seam on a borderless acrylic window.
        public const long WS_CAPTION = 0x00C00000;
        public const uint WM_NCCALCSIZE = 0x0083;
        public const nuint NonClientSubclassId = 1;
        public const uint SWP_NOSIZE = 0x0001;
        public const uint SWP_NOMOVE = 0x0002;
        public const uint SWP_NOZORDER = 0x0004;
        public const uint SWP_NOACTIVATE = 0x0010;
        public const uint SWP_FRAMECHANGED = 0x0020;
        public const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
        public const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        public const int DWMWCP_ROUND = 2;
        public const int DWMWA_BORDER_COLOR = 34;
        public const int DWMWA_COLOR_NONE = unchecked((int)0xFFFFFFFE);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
        public static extern nint GetWindowLongPtr(nint hWnd, int nIndex);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        public delegate nint SUBCLASSPROC(
            nint hWnd,
            uint message,
            nint wParam,
            nint lParam,
            nuint subclassId,
            nuint referenceData);

        [DllImport("comctl32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetWindowSubclass(
            nint hWnd, SUBCLASSPROC subclassProc, nuint subclassId, nuint referenceData);

        [DllImport("comctl32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool RemoveWindowSubclass(
            nint hWnd, SUBCLASSPROC subclassProc, nuint subclassId);

        [DllImport("comctl32.dll")]
        public static extern nint DefSubclassProc(
            nint hWnd, uint message, nint wParam, nint lParam);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
        public static extern nint SetWindowLongPtr(nint hWnd, int nIndex, nint dwNewLong);

        [DllImport("user32.dll")]
        public static extern uint GetDpiForWindow(nint hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetWindowPos(
            nint hWnd, nint hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

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
