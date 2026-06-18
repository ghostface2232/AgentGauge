using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using CommunityToolkit.Mvvm.Input;
using H.NotifyIcon;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Win32;
using Windows.UI.ViewManagement;

namespace Gauge.Services;

/// <summary>
/// Owns the system-tray icon and its interactions.
///
/// Implementation path: <b>H.NotifyIcon.WinUI</b> (2.4.1). It builds and resolves
/// cleanly against Windows App SDK 2.1.3 with no version downgrade, so the
/// CsWin32 / Shell_NotifyIcon fallback described in AGENTS.md was not needed.
///
/// The icon is created without a visual tree (<see cref="TaskbarIcon.ForceCreate"/>)
/// because Gauge has no visible window. Both the icon bitmap and the tooltip are
/// updatable at runtime so a later step can recolor the icon by usage level and
/// refresh the tooltip summary.
///
/// Context menu uses <see cref="ContextMenuMode.SecondWindow"/> for the clean WinUI
/// look. That mode hosts the menu in a separate window and hides it the instant the
/// window reports <c>Deactivated</c>; for a tray-only app that never owns the
/// foreground, Windows' foreground-lock throttles the library's SetForegroundWindow
/// call, so the menu auto-dismissed while hovering. We work around it by zeroing the
/// per-user foreground-lock timeout at startup (restored on dispose).
/// </summary>
public sealed class TrayIconService : IDisposable
{
    // Right-aligned indicator shown after the "start on boot" label when enabled.
    // Keeps the left edge of every item clean (no reserved check/icon column).
    private const string CheckedGlyph = "✓";

    // Tray icon assets. The default has dark lines (for a light taskbar); the dark
    // variant has white lines (for a dark taskbar).
    private const string LightThemeIconFile = "gauge_icon.ico";
    private const string DarkThemeIconFile = "gauge_icon_dark.ico";

    // Minimum width applied to each menu item. The item's text column then
    // stretches to fill it, pushing the right-aligned indicator to the far edge
    // so it sits in its own space instead of crowding the label. Applied per item
    // because SecondWindow mode does not reliably honor MenuFlyoutPresenter.MinWidth.
    private const double ItemMinWidth = 220;

    // Gap (physical px) kept between the menu's bottom edge and the taskbar when we
    // reposition it above the tray icon. Larger = menu sits higher. Tune to taste.
    private const int MenuBottomMargin = 16;
    // Side margin so the menu never touches the screen edge after clamping.
    private const int MenuSideMargin = 8;

    private readonly TaskbarIcon _trayIcon;
    private readonly MenuFlyoutItem _startOnBootItem;
    private readonly DispatcherQueue? _dispatcher = DispatcherQueue.GetForCurrentThread();
    // Held to keep the ColorValuesChanged subscription alive for live theme switches.
    private readonly UISettings _uiSettings = new();

    // Held so we can dispose the previous GDI icon handle when swapping icons.
    private Icon? _currentIcon;
    // We own the start-on-boot state and reflect it via right-aligned text.
    private bool _startOnBoot;
    // Saved so we can restore the user's foreground-lock setting on exit.
    private uint? _previousForegroundLockTimeout;
    private bool _disposed;

    /// <summary>Raised on left-click. Next step wires this to the popover toggle.</summary>
    public event EventHandler? LeftClicked;

    /// <summary>Context menu: "시작프로그램 등록" toggled. Argument is the new desired state.</summary>
    public event EventHandler<bool>? StartOnBootToggled;

    /// <summary>Context menu: "종료".</summary>
    public event EventHandler? ExitRequested;

    public TrayIconService()
    {
        // Make SetForegroundWindow succeed so the SecondWindow menu stays active.
        DisableForegroundLock();

        _startOnBootItem = new MenuFlyoutItem { Text = "시작프로그램 등록", MinWidth = ItemMinWidth };
        _startOnBootItem.Click += (_, _) =>
        {
            _startOnBoot = !_startOnBoot;
            UpdateStartOnBootIndicator();
            StartOnBootToggled?.Invoke(this, _startOnBoot);
            // The menu closes on click (standard WinUI menu behavior). The ✓ persists,
            // so reopening the menu confirms the current state.
        };

        _trayIcon = new TaskbarIcon
        {
            ToolTipText = "Gauge",
            // Fire left-click immediately instead of waiting out the double-click
            // interval — a single click should toggle the popover with no lag.
            NoLeftClickDelay = true,
            // SecondWindow renders the WinUI MenuFlyout (clean look) rather than a
            // native menu. The foreground-lock workaround above keeps it from
            // auto-dismissing. (Fallback if it ever misbehaves: ContextMenuMode.PopupMenu.)
            ContextMenuMode = ContextMenuMode.SecondWindow,
            LeftClickCommand = new RelayCommand(() => LeftClicked?.Invoke(this, EventArgs.Empty)),
            // The library positions the SecondWindow menu from the cursor (it ends up
            // up-and-right of the icon). We nudge it after show to sit above the icon
            // and just above the taskbar — see RepositionContextMenuAboveTray.
            RightClickCommand = new RelayCommand(ScheduleContextMenuReposition),
            ContextFlyout = BuildContextMenu(),
        };

        LoadInitialIcon();

        // Create the Shell_NotifyIcon entry now; the icon is not in a visual tree.
        _trayIcon.ForceCreate(enablesEfficiencyMode: false);

        // Swap the icon live when the system (taskbar) theme changes.
        _uiSettings.ColorValuesChanged += OnColorValuesChanged;
    }

    /// <summary>
    /// Reflects the current desired start-on-boot state in the menu indicator.
    /// Wiring to actual startup registration comes in a later step.
    /// </summary>
    public void SetStartOnBootChecked(bool isChecked)
    {
        _startOnBoot = isChecked;
        UpdateStartOnBootIndicator();
    }

    /// <summary>
    /// Updates the tooltip with a last-updated time and a short usage summary.
    /// Shell tooltips are length-limited (~127 chars), so keep <paramref name="summary"/> brief.
    /// </summary>
    public void UpdateToolTip(string summary, DateTimeOffset lastUpdated)
    {
        var text = $"Gauge — {summary}\n갱신: {lastUpdated.ToLocalTime():yyyy-MM-dd HH:mm}";
        _trayIcon.ToolTipText = text.Length > 127 ? text[..127] : text;
    }

    /// <summary>
    /// Swaps the tray icon bitmap at runtime. A later step will pass a freshly
    /// rendered icon recolored/badged for the current usage level.
    /// </summary>
    public void UpdateIcon(Icon icon)
    {
        ArgumentNullException.ThrowIfNull(icon);
        var previous = _currentIcon;
        _currentIcon = icon;
        _trayIcon.UpdateIcon(icon);
        // Dispose the old GDI handle after the swap so we don't leak it.
        if (!ReferenceEquals(previous, icon))
        {
            previous?.Dispose();
        }
    }

    private void UpdateStartOnBootIndicator()
    {
        // Right-aligned secondary text; empty when off so nothing shows on the left.
        _startOnBootItem.KeyboardAcceleratorTextOverride = _startOnBoot ? CheckedGlyph : string.Empty;
    }

    private void ScheduleContextMenuReposition()
    {
        // The menu window is created, positioned, and shown by the library during
        // this same right-click. Run our adjustment afterwards on the UI queue so the
        // window already exists and is foreground (best-effort; SecondWindow exposes
        // no placement API, so we move the window directly via Win32).
        _dispatcher?.TryEnqueue(DispatcherQueuePriority.Low, RepositionContextMenuAboveTray);
    }

    private static void RepositionContextMenuAboveTray()
    {
        // After the foreground-lock workaround, the library's SetForegroundWindow
        // makes the menu window the foreground window, so this is it.
        var hwnd = NativeMethods.GetForegroundWindow();
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        // Only ever move a window owned by our own process.
        _ = NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
        if (pid != NativeMethods.GetCurrentProcessId())
        {
            return;
        }

        if (!NativeMethods.GetWindowRect(hwnd, out var rect) ||
            !NativeMethods.GetCursorPos(out var cursor))
        {
            return;
        }

        var monitor = NativeMethods.MonitorFromPoint(cursor, NativeMethods.MONITOR_DEFAULTTONEAREST);
        var info = new NativeMethods.MONITORINFO { cbSize = Marshal.SizeOf<NativeMethods.MONITORINFO>() };
        if (!NativeMethods.GetMonitorInfo(monitor, ref info))
        {
            return;
        }

        var work = info.rcWork; // excludes the taskbar
        var width = rect.right - rect.left;
        var height = rect.bottom - rect.top;

        // Bottom edge just above the taskbar (lifted by MenuBottomMargin), centered
        // horizontally over the icon and clamped inside the work area.
        var top = work.bottom - MenuBottomMargin - height;
        var left = cursor.X - (width / 2);
        if (left + width > work.right - MenuSideMargin)
        {
            left = work.right - MenuSideMargin - width;
        }
        if (left < work.left + MenuSideMargin)
        {
            left = work.left + MenuSideMargin;
        }

        _ = NativeMethods.SetWindowPos(
            hwnd, IntPtr.Zero, left, top, 0, 0,
            NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
    }

    private void LoadInitialIcon()
    {
        if (LoadThemedIcon() is { } icon)
        {
            _currentIcon = icon;
            _trayIcon.Icon = icon; // set before ForceCreate; runtime swaps use UpdateIcon
        }
    }

    private void OnColorValuesChanged(UISettings sender, object args)
    {
        // ColorValuesChanged fires off the UI thread; marshal back before touching
        // the tray icon. Re-read the theme and swap to the matching asset.
        _dispatcher?.TryEnqueue(() =>
        {
            if (_disposed)
            {
                return;
            }

            if (LoadThemedIcon() is { } icon)
            {
                UpdateIcon(icon);
            }
        });
    }

    private Icon? LoadThemedIcon()
    {
        var fileName = IsTaskbarDarkTheme() ? DarkThemeIconFile : LightThemeIconFile;
        var path = Path.Combine(AppContext.BaseDirectory, "Assets", fileName);
        if (File.Exists(path))
        {
            return new Icon(path);
        }

        // Fall back to the default icon if the themed variant is missing.
        var fallback = Path.Combine(AppContext.BaseDirectory, "Assets", LightThemeIconFile);
        if (File.Exists(fallback))
        {
            return new Icon(fallback);
        }

        Debug.WriteLine($"[Gauge] Tray icon asset not found: {path}");
        return null;
    }

    /// <summary>
    /// True when the taskbar uses the dark theme. The tray icon lives on the taskbar,
    /// so we read SystemUsesLightTheme (the system/taskbar mode), not the app mode.
    /// </summary>
    private static bool IsTaskbarDarkTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            // Value missing → Windows treats it as light.
            return key?.GetValue("SystemUsesLightTheme") is int value && value == 0;
        }
        catch
        {
            return false;
        }
    }

    private MenuFlyout BuildContextMenu()
    {
        var exit = new MenuFlyoutItem { Text = "종료", MinWidth = ItemMinWidth };
        exit.Click += (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty);

        var menu = new MenuFlyout();
        menu.Items.Add(_startOnBootItem);
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(exit);
        return menu;
    }

    private void DisableForegroundLock()
    {
        uint current = 0;
        if (NativeMethods.SystemParametersInfoGet(
                NativeMethods.SPI_GETFOREGROUNDLOCKTIMEOUT, 0, ref current, 0))
        {
            _previousForegroundLockTimeout = current;
        }

        _ = NativeMethods.SystemParametersInfoSet(
            NativeMethods.SPI_SETFOREGROUNDLOCKTIMEOUT, 0, IntPtr.Zero, NativeMethods.SPIF_SENDCHANGE);
    }

    private void RestoreForegroundLock()
    {
        if (_previousForegroundLockTimeout is uint previous)
        {
            _ = NativeMethods.SystemParametersInfoSet(
                NativeMethods.SPI_SETFOREGROUNDLOCKTIMEOUT, 0, (IntPtr)previous, NativeMethods.SPIF_SENDCHANGE);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;

        _uiSettings.ColorValuesChanged -= OnColorValuesChanged;
        RestoreForegroundLock();
        _trayIcon.Dispose();
        _currentIcon?.Dispose();
        _currentIcon = null;
    }

    private static class NativeMethods
    {
        public const uint SPI_GETFOREGROUNDLOCKTIMEOUT = 0x2000;
        public const uint SPI_SETFOREGROUNDLOCKTIMEOUT = 0x2001;
        public const uint SPIF_SENDCHANGE = 0x02;

        public const uint MONITOR_DEFAULTTONEAREST = 0x2;
        public const uint SWP_NOSIZE = 0x0001;
        public const uint SWP_NOZORDER = 0x0004;
        public const uint SWP_NOACTIVATE = 0x0010;

        // GET writes the current timeout into pvParam (a DWORD by reference).
        [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SystemParametersInfoGet(
            uint uiAction, uint uiParam, ref uint pvParam, uint fWinIni);

        // SET passes the new timeout as the pvParam value itself (cast to UINT).
        [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SystemParametersInfoSet(
            uint uiAction, uint uiParam, IntPtr pvParam, uint fWinIni);

        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll", SetLastError = true)]
        public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("kernel32.dll")]
        public static extern uint GetCurrentProcessId();

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetCursorPos(out POINT lpPoint);

        [DllImport("user32.dll")]
        public static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

        [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetWindowPos(
            IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int left;
            public int top;
            public int right;
            public int bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct MONITORINFO
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
        }
    }
}
