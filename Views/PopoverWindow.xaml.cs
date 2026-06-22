using System.Runtime.InteropServices;
using Gauge.Localization;
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
    // Reserve for the title header + footer + paddings when capping the scrollable
    // body so the whole popover stays within the work area on very tall content.
    private const double FooterChromeAllowanceDip = 148;
    // Bottom inset added below the last card when the body does NOT scroll, so the gap
    // above the footer matches the header's 8dip bottom padding (Padding="16,16,16,8").
    // When the body overflows and scrolls, no inset is added: content stays flush to the
    // footer divider and scrolls out of sight beneath it.
    private const double BodyBottomBreathingRoomDip = 8;

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
    private int _titleIconLoadId;
    private string? _titleIconKey;
    private AutoHideScrollBar? _usageAutoHide;
    private AutoHideScrollBar? _settingsAutoHide;

    // Live drag-to-reorder for the two service lists. Created once per host (the usage list's
    // items panel once it loads; the settings repeater up front).
    private ReorderSurface? _usageReorder;
    private ReorderSurface? _authReorder;

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
        RootHost.ActualThemeChanged += (_, _) =>
        {
            UpdateDwmTheme();
            _ = UpdateTitleIcon();
        };
        _ = UpdateTitleIcon();

        // Start with DWM rounded corners. To switch to a larger radius later, make the
        // window background transparent and round RootBorder instead (its CornerRadius
        // is already set below); keep ApplyDwmRoundedCorners off in that mode.
        ApplyDwmRoundedCorners();
        UpdateDwmTheme();
        RootBorder.CornerRadius = new CornerRadius(CornerRadiusDip);

        // Resize the window to match content height as it loads/changes (no filler).
        RootBorder.SizeChanged += OnContentSizeChanged;
        SettingsBorder.SizeChanged += OnContentSizeChanged;

        // Scrollbars reveal while scrolling and hide ~1s after it stops, so they don't sit
        // permanently over the cards' right edge.
        _usageAutoHide = new AutoHideScrollBar(BodyScroll);
        _settingsAutoHide = new AutoHideScrollBar(SettingsScroll);

        // Press-and-drag reorder for the settings service grid. The usage list's surface is
        // created in OnUsagePanelLoaded (its items panel is built by the ItemsControl template).
        _authReorder = new ReorderSurface(
            AuthRepeater,
            () => (SettingsBorder.DataContext as SettingsViewModel)?.Authentication.Count ?? 0,
            index => AuthRepeater.TryGetElement(index) as FrameworkElement,
            (from, to) =>
            {
                if (SettingsBorder.DataContext is not SettingsViewModel vm) return;
                // Finalize the visible order immediately; persist + sync the other screen on the
                // next tick so the drop stays snappy (the order is already correct on screen).
                if (from != to) vm.Authentication.Move(from, to);
                SettingsBorder.DispatcherQueue.TryEnqueue(
                    () => vm.ReorderTools(vm.Authentication.Select(c => c.Tool).ToList()));
            });

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

        // Re-decode the title mark for the monitor we're about to open on: the target
        // physical size depends on this monitor's DPI, which CaptureTargetMonitor just
        // refreshed. The key-dedupe inside makes this a no-op when the scale is unchanged.
        _ = UpdateTitleIcon();

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

        // Flash the scrollbar once content has settled, hinting the list scrolls, then auto-hide.
        RootHost.DispatcherQueue.TryEnqueue(() => _usageAutoHide?.Reveal());
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
        RootHost.DispatcherQueue.TryEnqueue(() => _settingsAutoHide?.Reveal());
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
            flyout.Items.Add(new MenuFlyoutItem { Text = Loc.Get("NoServicesToAdd"), IsEnabled = false });
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

    // Build the usage list's reorder surface once its items panel exists. The ItemsControl
    // creates the StackPanel from its ItemsPanelTemplate, so we can't reference it until Loaded.
    private void OnUsagePanelLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not StackPanel panel || _usageReorder is not null)
        {
            return;
        }
        _usageReorder = new ReorderSurface(
            panel,
            () => panel.Children.Count,
            index => index >= 0 && index < panel.Children.Count ? panel.Children[index] as FrameworkElement : null,
            (from, to) =>
            {
                if (RootHost.DataContext is not UsageViewModel vm) return;
                // Finalize the visible order immediately; persist + sync the other screen on the
                // next tick so the drop stays snappy.
                if (from != to) vm.Cards.Move(from, to);
                RootHost.DispatcherQueue.TryEnqueue(
                    () => vm.ReorderTools(vm.Cards.Select(c => c.ToolName).ToList()));
            });
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
        var height = RootBorder.DesiredSize.Height;

        // BodyScroll's DesiredSize is clamped to its MaxHeight, so reaching the cap means
        // the cards overflow and scroll — keep them flush to the footer divider. Otherwise
        // the cards fit, so add a bottom inset matching the header's bottom padding so the
        // gap below the last card mirrors the gap above the first.
        var bodyScrolls = BodyScroll.DesiredSize.Height >= BodyScroll.MaxHeight - 0.5;
        if (!bodyScrolls)
        {
            height += BodyBottomBreathingRoomDip;
        }

        _usageViewHeightDip = height;
        _usageLayoutRefreshPending = false;
    }

    /// <summary>
    /// Sets the title mark for the current theme, pre-scaled to its exact on-screen pixel
    /// size via <see cref="IconDecoder"/>. _titleIconLoadId discards a load a newer
    /// theme/DPI change has superseded mid-await; _titleIconKey skips redundant reloads.
    /// </summary>
    private async Task UpdateTitleIcon()
    {
        var stem = RootHost.ActualTheme == ElementTheme.Dark ? "gauge_icon_dark" : "gauge_icon";
        var scale = TitleIcon.XamlRoot?.RasterizationScale ?? _scale;
        if (scale <= 0) scale = 1.0;
        var targetPx = (uint)Math.Max(1, Math.Round(TitleIcon.Width * scale));

        var key = $"{stem}@{targetPx}";
        if (key == _titleIconKey) return;

        var path = Path.Combine(AppContext.BaseDirectory, "Assets", $"{stem}.ico");
        var loadId = ++_titleIconLoadId;
        var source = await IconDecoder.LoadScaledAsync(path, targetPx);
        if (source is null || loadId != _titleIconLoadId) return;
        TitleIcon.Source = source;
        _titleIconKey = key;
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

    /// <summary>
    /// Reveals a <see cref="ScrollViewer"/>'s vertical scrollbar instantly while the view is
    /// changing and fades it out ~1s after scrolling stops. Driven by us rather than the system
    /// so the bar never sits permanently over the cards, independent of the OS "always show
    /// scrollbars" setting. The fade animates the scrollbar element's opacity; if it can't be
    /// located in the template, this degrades to a plain show/hide.
    /// </summary>
    private sealed class AutoHideScrollBar
    {
        private static readonly TimeSpan HideDelay = TimeSpan.FromSeconds(1);
        private const int FadeOutMs = 250;

        private readonly ScrollViewer _scroller;
        private readonly DispatcherTimer _timer;
        private ScrollBar? _bar;
        private Storyboard? _fadeOut;

        public AutoHideScrollBar(ScrollViewer scroller)
        {
            _scroller = scroller;
            _scroller.VerticalScrollBarVisibility = ScrollBarVisibility.Hidden;
            _timer = new DispatcherTimer { Interval = HideDelay };
            _timer.Tick += (_, _) =>
            {
                _timer.Stop();
                BeginFadeOut();
            };
            _scroller.ViewChanged += (_, _) => Reveal();
            _scroller.Loaded += (_, _) => _bar ??= FindVerticalScrollBar(_scroller);
        }

        public void Reveal()
        {
            _scroller.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            _bar ??= FindVerticalScrollBar(_scroller);
            _fadeOut?.Stop();
            _fadeOut = null;
            if (_bar is not null)
            {
                _bar.Opacity = 1; // instant on scroll — no fade-in lag
            }

            _timer.Stop();
            _timer.Start();
        }

        private void BeginFadeOut()
        {
            if (_bar is null)
            {
                _scroller.VerticalScrollBarVisibility = ScrollBarVisibility.Hidden;
                return;
            }

            var animation = new DoubleAnimation
            {
                To = 0,
                Duration = new Duration(TimeSpan.FromMilliseconds(FadeOutMs)),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            };
            Storyboard.SetTarget(animation, _bar);
            Storyboard.SetTargetProperty(animation, "Opacity");

            var storyboard = new Storyboard();
            storyboard.Children.Add(animation);
            storyboard.Completed += (_, _) =>
            {
                if (!ReferenceEquals(_fadeOut, storyboard))
                {
                    return; // superseded by a Reveal
                }

                _scroller.VerticalScrollBarVisibility = ScrollBarVisibility.Hidden;
                _bar!.Opacity = 1; // reset so the next reveal is fully opaque
                _fadeOut = null;
            };
            _fadeOut = storyboard;
            storyboard.Begin();
        }

        private static ScrollBar? FindVerticalScrollBar(DependencyObject root)
        {
            var count = VisualTreeHelper.GetChildrenCount(root);
            for (var i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                if (child is ScrollBar { Orientation: Orientation.Vertical } bar)
                {
                    return bar;
                }

                if (FindVerticalScrollBar(child) is { } found)
                {
                    return found;
                }
            }

            return null;
        }
    }

    /// <summary>
    /// Press-and-drag live reordering for one list of cards. The pressed card itself (not a
    /// ghost image) follows the pointer; the other cards animate aside in real time to open a
    /// gap at the insertion point — like reordering icons on a phone home screen. On release
    /// the card settles into the gap and the new order is committed (and persisted).
    ///
    /// Layout-agnostic: it snapshots each slot's laid-out position from the realized
    /// containers, so the same logic drives the usage list (a vertical StackPanel) and the
    /// settings grid (a 2-column ItemsRepeater). The host element receives the pointer events
    /// and is the pointer-capture target; the supplied accessors map slot index → container
    /// and commit a logical move.
    /// </summary>
    private sealed class ReorderSurface
    {
        private const double DragThreshold = 6;   // pointer travel (px) before a press becomes a drag
        private const int ShiftMs = 170;          // how long a card takes to slide aside / settle
        private const double Hysteresis = 14;     // px a slot must win by to steal the target (anti-flicker)

        private readonly UIElement _host;
        private readonly Func<int> _count;
        private readonly Func<int, FrameworkElement?> _container;
        private readonly Action<int, int> _commit;
        private readonly Dictionary<FrameworkElement, Storyboard> _running = new();

        private bool _pressed;
        private bool _dragging;
        private uint _pointerId;
        private int _from;
        private int _target;
        private Point _pressPoint;
        private Point[] _home = Array.Empty<Point>();   // each slot's top-left, in host coordinates
        private Point[] _centers = Array.Empty<Point>(); // each slot's center, for nearest-slot targeting
        private FrameworkElement? _dragged;

        public ReorderSurface(
            UIElement host, Func<int> count, Func<int, FrameworkElement?> container, Action<int, int> commit)
        {
            _host = host;
            _count = count;
            _container = container;
            _commit = commit;
            host.PointerPressed += OnPointerPressed;
            host.PointerMoved += OnPointerMoved;
            host.PointerReleased += OnPointerReleased;
            host.PointerCaptureLost += OnPointerEnded;
            host.PointerCanceled += OnPointerEnded;
        }

        private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
        {
            if (_pressed || _dragging)
            {
                return;
            }
            var pt = e.GetCurrentPoint(_host).Position;
            var idx = HitTest(pt);
            if (idx < 0)
            {
                return;
            }
            _pressed = true;
            _from = idx;
            _pressPoint = pt;
            _pointerId = e.Pointer.PointerId;
        }

        private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (!_pressed || e.Pointer.PointerId != _pointerId)
            {
                return;
            }
            var pt = e.GetCurrentPoint(_host).Position;

            if (!_dragging)
            {
                if (Math.Abs(pt.X - _pressPoint.X) < DragThreshold && Math.Abs(pt.Y - _pressPoint.Y) < DragThreshold)
                {
                    return; // still within the tap tolerance — let a click through
                }
                if (!BeginDrag(e.Pointer))
                {
                    _pressed = false;
                    return;
                }
            }

            var dx = pt.X - _pressPoint.X;
            var dy = pt.Y - _pressPoint.Y;

            // Keep the card within the list's content width so it never crosses into the
            // scroll viewer's edge padding (where it would be clipped). For the full-width
            // usage list this pins X to 0 (vertical-only drag); for the settings grid it lets
            // the card move between columns but not past the left/right edge.
            if (_dragged is not null)
            {
                var hostWidth = (_host as FrameworkElement)?.ActualWidth ?? double.MaxValue;
                var minDx = -_home[_from].X;
                var maxDx = hostWidth - _dragged.ActualWidth - _home[_from].X;
                dx = Math.Clamp(dx, Math.Min(minDx, maxDx), Math.Max(minDx, maxDx));

                // The pressed card tracks the pointer instantly (no animation).
                StopAnimation(_dragged);
                var t = Translate(_dragged);
                t.X = dx;
                t.Y = dy;
            }

            // Insertion point = the slot the dragged card is now nearest to. The card center
            // already reflects the clamped X (so it can reach the next column) plus the free Y.
            var center = new Point(_centers[_from].X + dx, _centers[_from].Y + dy);
            var target = ComputeTarget(center);
            if (target != _target)
            {
                _target = target;
                ShiftOthers();
            }
            e.Handled = true;
        }

        private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
        {
            if (!_pressed)
            {
                return;
            }
            var wasDragging = _dragging;
            var from = _from;
            var target = _target;
            _pressed = false;
            _host.ReleasePointerCaptures();
            if (!wasDragging)
            {
                _dragging = false;
                return; // a plain tap — nothing to reorder
            }
            e.Handled = true;
            Settle(from, target);
        }

        private void OnPointerEnded(object sender, PointerRoutedEventArgs e)
        {
            // Capture lost / canceled mid-drag: settle at the current target so the cards
            // aren't stranded mid-shift.
            if (!_dragging)
            {
                _pressed = false;
                return;
            }
            var from = _from;
            var target = _target;
            _pressed = false;
            Settle(from, target);
        }

        private bool BeginDrag(Pointer pointer)
        {
            var n = _count();
            if (n <= 1 || _from < 0 || _from >= n)
            {
                return false;
            }
            _home = new Point[n];
            _centers = new Point[n];
            for (var i = 0; i < n; i++)
            {
                var el = _container(i);
                _home[i] = el is null ? default : new Point(el.ActualOffset.X, el.ActualOffset.Y);
                _centers[i] = el is null
                    ? default
                    : new Point(el.ActualOffset.X + (el.ActualWidth / 2), el.ActualOffset.Y + (el.ActualHeight / 2));
            }
            _dragged = _container(_from);
            if (_dragged is null)
            {
                return false;
            }
            Canvas.SetZIndex(_dragged, 1); // lift above the cards it slides over
            _dragging = true;
            _target = _from;
            _host.CapturePointer(pointer);
            return true;
        }

        private void Settle(int from, int target)
        {
            _dragging = false;
            var slot = Math.Clamp(target, 0, Math.Max(0, _home.Length - 1));
            if (_dragged is null)
            {
                Commit(from, slot);
                return;
            }
            // Slide the card into its gap, then swap the visual transforms for the real order in
            // one synchronous step so there is no jump.
            AnimateTo(_dragged, _home[slot].X - _home[from].X, _home[slot].Y - _home[from].Y,
                () => Commit(from, slot));
        }

        private void Commit(int from, int slot)
        {
            ClearTransforms();
            _dragged = null;
            _commit(from, slot);
        }

        // Insertion slot for the dragged card: whichever home slot its center is now closest to
        // (so a card flows into a slot once it is over that slot's half, like a phone home
        // screen). A hysteresis band means a rival slot has to win by a clear margin before it
        // steals the target — that gives a little resistance so the order doesn't flicker when
        // the card hovers on a boundary.
        private int ComputeTarget(Point center)
        {
            var n = _count();
            if (n == 0)
            {
                return _target;
            }
            var current = Math.Clamp(_target, 0, n - 1);

            var nearest = current;
            var nearestDist = double.MaxValue;
            for (var i = 0; i < n; i++)
            {
                var dist = Distance(center, _centers[i]);
                if (dist < nearestDist)
                {
                    nearestDist = dist;
                    nearest = i;
                }
            }

            if (nearest == current)
            {
                return current;
            }
            // Only switch if the new slot is closer than the current one by the hysteresis margin.
            return nearestDist + Hysteresis < Distance(center, _centers[current]) ? nearest : current;
        }

        private static double Distance(Point a, Point b)
        {
            var dx = a.X - b.X;
            var dy = a.Y - b.Y;
            return Math.Sqrt((dx * dx) + (dy * dy));
        }

        // Animate every non-dragged card to the slot it occupies once the dragged card is
        // inserted at _target, opening (and closing) the gap live.
        private void ShiftOthers()
        {
            var n = _count();
            var order = new List<int>(n);
            for (var i = 0; i < n; i++)
            {
                if (i != _from)
                {
                    order.Add(i);
                }
            }
            order.Insert(Math.Clamp(_target, 0, order.Count), _from);

            for (var slot = 0; slot < order.Count; slot++)
            {
                var logical = order[slot];
                if (logical == _from)
                {
                    continue;
                }
                var el = _container(logical);
                if (el is null)
                {
                    continue;
                }
                AnimateTo(el, _home[slot].X - _home[logical].X, _home[slot].Y - _home[logical].Y);
            }
        }

        private int HitTest(Point pt)
        {
            for (var i = 0; i < _count(); i++)
            {
                var el = _container(i);
                if (el is null)
                {
                    continue;
                }
                double x = el.ActualOffset.X;
                double y = el.ActualOffset.Y;
                if (pt.X >= x && pt.X <= x + el.ActualWidth && pt.Y >= y && pt.Y <= y + el.ActualHeight)
                {
                    return i;
                }
            }
            return -1;
        }

        private static TranslateTransform Translate(FrameworkElement el)
        {
            if (el.RenderTransform is TranslateTransform existing)
            {
                return existing;
            }
            var transform = new TranslateTransform();
            el.RenderTransform = transform;
            return transform;
        }

        private void AnimateTo(FrameworkElement el, double x, double y, Action? completed = null)
        {
            var t = Translate(el);
            // Capture the currently-held values before stopping, so the new run starts from
            // where the card actually is (no snap-to-base flicker when retargeting mid-shift).
            var fromX = t.X;
            var fromY = t.Y;
            StopAnimation(el);

            var sb = new Storyboard();
            sb.Children.Add(Anim(t, "X", fromX, x));
            sb.Children.Add(Anim(t, "Y", fromY, y));
            sb.Completed += (_, _) =>
            {
                if (_running.TryGetValue(el, out var current) && ReferenceEquals(current, sb))
                {
                    _running.Remove(el);
                }
                completed?.Invoke();
            };
            _running[el] = sb;
            sb.Begin();
        }

        private static DoubleAnimation Anim(TranslateTransform target, string property, double from, double to)
        {
            var animation = new DoubleAnimation
            {
                From = from,
                To = to,
                Duration = new Duration(TimeSpan.FromMilliseconds(ShiftMs)),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            };
            Storyboard.SetTarget(animation, target);
            Storyboard.SetTargetProperty(animation, property);
            return animation;
        }

        private void StopAnimation(FrameworkElement el)
        {
            if (_running.TryGetValue(el, out var sb))
            {
                sb.Stop();
                _running.Remove(el);
            }
        }

        private void ClearTransforms()
        {
            for (var i = 0; i < _count(); i++)
            {
                var el = _container(i);
                if (el is null)
                {
                    continue;
                }
                StopAnimation(el);
                if (el.RenderTransform is TranslateTransform t)
                {
                    t.X = 0;
                    t.Y = 0;
                }
                Canvas.SetZIndex(el, 0);
            }
            _running.Clear();
        }
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
