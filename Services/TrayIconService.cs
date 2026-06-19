using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using CommunityToolkit.Mvvm.Input;
using H.NotifyIcon;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
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
/// per-user foreground-lock timeout at startup. It is restored on dispose, with an
/// <see cref="AppDomain.ProcessExit"/> safety net so a crash that bypasses dispose
/// does not leave the global setting pinned at 0 for the rest of the login session.
/// </summary>
public sealed class TrayIconService : IDisposable
{
    // Right-aligned indicator shown after the "start on boot" label when enabled.
    // Keeps the left edge of every item clean (no reserved check/icon column).
    private const string CheckedGlyph = "✓";

    // Tray icon assets. Two dimensions:
    //   • taskbar theme — the light stem has dark lines (for a light taskbar), the
    //     dark stem has white lines (for a dark taskbar);
    //   • usage level — a "_70" / "_90" suffix marks the caution/danger variants.
    // Final file name = "{stem}{levelSuffix}.ico", e.g. gauge_icon_dark_90.ico.
    private const string LightIconStem = "gauge_icon";
    private const string DarkIconStem = "gauge_icon_dark";

    // Usage thresholds that select the icon variant. These match the asset file
    // names (_70 / _90), matching the popover's usage-level thresholds.
    private const double CautionRatio = 0.70;
    private const double DangerRatio = 0.90;

    // Minimum width applied to each menu item. The item's text column then
    // stretches to fill it, pushing the right-aligned indicator to the far edge
    // so it sits in its own space instead of crowding the label. Applied per item
    // because SecondWindow mode does not reliably honor MenuFlyoutPresenter.MinWidth.
    private const double ItemMinWidth = 220;

    private const int MenuBottomMargin = 16;
    private const int MenuSideMargin = 8;

    private readonly TaskbarIcon _trayIcon;
    private readonly MenuFlyoutItem _startOnBootItem;
    private readonly DispatcherQueue? _dispatcher = DispatcherQueue.GetForCurrentThread();
    // Held to keep the ColorValuesChanged subscription alive for live theme switches.
    private readonly UISettings _uiSettings = new();

    // Current usage-level suffix ("" / "_70" / "_90"). Combined with the taskbar
    // theme to pick the icon asset; survives theme switches.
    private string _levelSuffix = string.Empty;

    // Held so we can dispose the previous GDI icon handle when swapping icons.
    private Icon? _currentIcon;
    // We own the start-on-boot state and reflect it via right-aligned text.
    private bool _startOnBoot;
    // Saved so we can restore the user's foreground-lock setting on exit. Guarded by
    // _foregroundLockGate because Dispose and the ProcessExit handler can race.
    private readonly object _foregroundLockGate = new();
    private uint? _previousForegroundLockTimeout;
    // Held so we can unsubscribe the ProcessExit safety net on dispose.
    private readonly EventHandler _processExitHandler;
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

        // Normal exits (tray "종료", update restart) restore the lock through Dispose.
        // A crash never reaches Dispose, so without this the global timeout would stay
        // at 0 — altering focus behavior for every other app — until the next sign-in.
        // ProcessExit runs on CLR shutdown, including the unhandled-exception path, and
        // only fires as the process is ending, so it cannot affect Gauge's own behavior.
        // RestoreForegroundLock is idempotent, so the Dispose + ProcessExit overlap on a
        // normal exit is harmless.
        _processExitHandler = (_, _) => RestoreForegroundLock();
        AppDomain.CurrentDomain.ProcessExit += _processExitHandler;

        _startOnBootItem = new MenuFlyoutItem
        {
            Text = "시작프로그램 등록",
            MinWidth = ItemMinWidth,
            // WinUI's default 9/10 vertical padding overflows H.NotifyIcon's
            // fractional-DPI SecondWindow viewport by a few pixels. Preserve the
            // native visual while reclaiming 3 DIP per item.
            Padding = new Thickness(11, 8, 11, 8),
        };
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
            ContextMenuMode = ContextMenuMode.SecondWindow,
            LeftClickCommand = new RelayCommand(() => LeftClicked?.Invoke(this, EventArgs.Empty)),
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
    /// Updates the tray icon to reflect the highest usage ratio across all tools:
    /// normal below 70%, the caution variant at ≥70%, the danger variant at ≥90%.
    /// No-op (no GDI churn) when the resulting variant is unchanged.
    /// </summary>
    public void UpdateUsageLevel(double highestRatio)
    {
        var suffix = SuffixForRatio(highestRatio);
        if (suffix == _levelSuffix)
        {
            return;
        }

        _levelSuffix = suffix;
        if (LoadThemedIcon() is { } icon)
        {
            UpdateIcon(icon);
        }
    }

    private static string SuffixForRatio(double ratio)
    {
        if (double.IsNaN(ratio))
        {
            ratio = 0;
        }

        if (ratio >= DangerRatio)
        {
            return "_90";
        }

        return ratio >= CautionRatio ? "_70" : string.Empty;
    }

    /// <summary>
    /// Swaps the tray icon bitmap at runtime (used by both the usage-level and the
    /// taskbar-theme paths). Disposes the previous GDI handle after the swap.
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
        _dispatcher?.TryEnqueue(DispatcherQueuePriority.Low, RepositionContextMenuAboveTray);
    }

    private static void RepositionContextMenuAboveTray()
    {
        var hwnd = NativeMethods.GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return;

        _ = NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
        if (pid != NativeMethods.GetCurrentProcessId()) return;

        if (!NativeMethods.GetWindowRect(hwnd, out var rect) ||
            !NativeMethods.GetCursorPos(out var cursor)) return;

        var monitor = NativeMethods.MonitorFromPoint(cursor, NativeMethods.MONITOR_DEFAULTTONEAREST);
        var info = new NativeMethods.MONITORINFO { cbSize = Marshal.SizeOf<NativeMethods.MONITORINFO>() };
        if (!NativeMethods.GetMonitorInfo(monitor, ref info)) return;

        var work = info.rcWork;
        var width = rect.right - rect.left;
        var height = rect.bottom - rect.top;
        var top = work.bottom - MenuBottomMargin - height;
        var left = cursor.X - (width / 2);
        if (left + width > work.right - MenuSideMargin)
            left = work.right - MenuSideMargin - width;
        if (left < work.left + MenuSideMargin)
            left = work.left + MenuSideMargin;

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
        var stem = IsTaskbarDarkTheme() ? DarkIconStem : LightIconStem;

        // Preferred asset for the current theme + usage level, then progressively
        // looser fallbacks: same theme without the level suffix, then the light
        // default. Keeps the icon sensible even if a variant is missing.
        foreach (var fileName in new[] { $"{stem}{_levelSuffix}.ico", $"{stem}.ico", $"{LightIconStem}.ico" })
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Assets", fileName);
            if (File.Exists(path))
            {
                return new Icon(path);
            }
        }

        Debug.WriteLine($"[Gauge] Tray icon asset not found for stem '{stem}{_levelSuffix}'.");
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
        var exit = new MenuFlyoutItem
        {
            Text = "종료",
            MinWidth = ItemMinWidth,
            Padding = new Thickness(11, 8, 11, 8),
        };
        exit.Click += (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty);

        var menu = new MenuFlyout();
        // Disable the presenter's internal scrolling so the SecondWindow host never
        // shows a scrollbar / allows a few-px scroll when its window lands a couple
        // physical pixels short of the content at fractional DPI.
        if (Application.Current.Resources.TryGetValue("GaugeMenuFlyoutPresenterStyle", out var presenterStyle)
            && presenterStyle is Style style)
        {
            menu.MenuFlyoutPresenterStyle = style;
        }
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
        // Dispose (UI thread) and the ProcessExit safety net (CLR shutdown thread) can
        // both reach here. Take the saved value once under the lock and clear it so the
        // restore runs exactly once.
        uint previous;
        lock (_foregroundLockGate)
        {
            if (_previousForegroundLockTimeout is not uint saved)
            {
                return;
            }
            previous = saved;
            _previousForegroundLockTimeout = null;
        }

        _ = NativeMethods.SystemParametersInfoSet(
            NativeMethods.SPI_SETFOREGROUNDLOCKTIMEOUT, 0, (IntPtr)previous, NativeMethods.SPIF_SENDCHANGE);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;

        _uiSettings.ColorValuesChanged -= OnColorValuesChanged;
        AppDomain.CurrentDomain.ProcessExit -= _processExitHandler;
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
