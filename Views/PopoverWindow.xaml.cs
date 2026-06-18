using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
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
    private const double PopoverHeightDip = 480;
    private const double EdgeMarginDip = 12;
    private const double CornerRadiusDip = 8;

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
        PositionBottomRight();
        _isShown = true;
        AppWindow.Show(activateWindow: true);
        Activate();
        RootHost.Focus(FocusState.Programmatic); // so ESC and light dismiss work
        PlayShowAnimation();
    }

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

    private void PositionBottomRight()
    {
        // Anchor to the monitor under the cursor (where the tray click happened) so we
        // follow the user across monitors. WorkArea excludes the taskbar wherever it is.
        NativeMethods.GetCursorPos(out var cursor);
        var area = DisplayArea.GetFromPoint(new PointInt32(cursor.X, cursor.Y), DisplayAreaFallback.Nearest);
        var work = area.WorkArea;

        // WorkArea is in physical pixels; convert our DIP sizes with the window's DPI.
        var scale = NativeMethods.GetDpiForWindow(_hwnd) / 96.0;
        var width = (int)Math.Round(PopoverWidthDip * scale);
        var height = (int)Math.Round(PopoverHeightDip * scale);
        var margin = (int)Math.Round(EdgeMarginDip * scale);

        var x = work.X + work.Width - width - margin;
        var y = work.Y + work.Height - height - margin;
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
