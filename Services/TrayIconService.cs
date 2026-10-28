using System.Globalization;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using CommunityToolkit.Mvvm.Input;
using Gauge.Localization;
using Gauge.Models;
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
/// updatable at runtime, so the icon can be recolored by usage level and the tooltip
/// summary refreshed.
///
/// Context menu uses <see cref="ContextMenuMode.SecondWindow"/> for the clean WinUI
/// look. That mode hosts the menu in a separate window and hides it the instant the
/// window reports <c>Deactivated</c>; for a tray-only app that never owns the
/// foreground, Windows' foreground-lock throttles the library's SetForegroundWindow
/// call, so the menu auto-dismissed while hovering. We work around it by zeroing the
/// per-user foreground-lock timeout at startup via <see cref="ForegroundLockGuard"/>,
/// which persists the user's value so even a hard kill cannot lose it. It is restored
/// on dispose, with an <see cref="AppDomain.ProcessExit"/> safety net so a crash that
/// bypasses dispose does not leave the global setting pinned at 0 for the rest of the
/// login session.
///
/// The menu carries one item per notification kind — the same two the settings card
/// shows — rather than a single master pause. A master that lived only here could silence
/// alerts the settings card still reported as on; mirroring the identical two switches
/// keeps the surfaces incapable of disagreeing.
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
    // H.NotifyIcon builds its SecondWindow host before the flyout has a XamlRoot, so on the
    // first open part of the content is measured at scale 1.0 and the window lands short of
    // the presenter at any DPI above 100% (measured: 202 px against 212 px at 150%, the
    // later opens being right). That part is about 20 DIP, so the first open is grown by it
    // scaled up to the window's DPI. Later opens are left alone.
    private const double FirstOpenUnscaledDip = 20;

    private readonly TaskbarIcon _trayIcon;
    private readonly MenuFlyoutItem _thresholdAlertsItem;
    private readonly MenuFlyoutItem _resetAlertsItem;
    private readonly MenuFlyoutItem _startOnBootItem;
    private readonly DispatcherQueue? _dispatcher = DispatcherQueue.GetForCurrentThread();
    // Held to keep the ColorValuesChanged subscription alive for live theme switches.
    private readonly UISettings _uiSettings = new();

    // Current usage-level suffix ("" / "_70" / "_90"). Combined with the taskbar
    // theme to pick the icon asset; survives theme switches.
    private string _levelSuffix = string.Empty;

    // The chosen tool's ring, or null for the classic overall gauge. While set, the level
    // suffix is still tracked so clearing the target falls back to the right gauge variant.
    private StatusRingState? _ring;
    private UsageDisplayBasis _ringBasis;
    private readonly TrayRingIconRenderer _ringRenderer = new();
    // Inputs of the ring icon on screen, so an unchanged refresh causes no GDI churn.
    private (StatusRingState Ring, UsageDisplayBasis Basis, int Px, bool Dark)? _ringDrawn;

    // Held so we can dispose the previous GDI icon handle when swapping icons.
    private Icon? _currentIcon;
    // ICO sources carry their size frames; DPI does not change this source-cache key.
    private readonly Dictionary<string, Icon> _iconCache = new();
    // We own the start-on-boot state and reflect it via right-aligned text.
    private bool _startOnBoot;
    // Mirror of the per-kind notification state, reflected the same way.
    private NotificationPreferences _notifications = NotificationPreferences.Default;
    // Owns zeroing the foreground-lock timeout and restoring the user's value on exit
    // (once, even when Dispose and the ProcessExit handler race).
    private readonly ForegroundLockGuard _foregroundLock = new();
    // False for a demo instance running beside the real one: the guard is per-user global
    // state, so a second owner would restore the user's timeout under the main instance.
    private readonly bool _manageForegroundLock;
    // Held so we can unsubscribe the ProcessExit safety net on dispose.
    private readonly EventHandler _processExitHandler;
    private bool _contextMenuMeasured;
    private bool _disposed;

    /// <summary>Raised on left-click; wired to the popover toggle.</summary>
    public event EventHandler? LeftClicked;

    /// <summary>Context menu: "시작프로그램 등록" toggled. Argument is the new desired state.</summary>
    public event EventHandler<bool>? StartOnBootToggled;

    /// <summary>
    /// Context menu: one of the notification-kind items toggled. Like the other menu
    /// toggles this only reports intent — the owner applies and persists the change, then
    /// reflects the real result back through <see cref="SetNotificationPreferences"/>.
    /// </summary>
    public event EventHandler<(UsageNotificationKind Kind, bool Enabled)>? NotificationKindToggled;

    /// <summary>Context menu: "종료".</summary>
    public event EventHandler? ExitRequested;

    public TrayIconService(bool manageForegroundLock = true)
    {
        _manageForegroundLock = manageForegroundLock;
        // Make SetForegroundWindow succeed so the SecondWindow menu stays active.
        if (_manageForegroundLock) _foregroundLock.Disable();

        // Normal exits (tray "종료", update restart) restore the lock through Dispose.
        // A crash never reaches Dispose, so without this the global timeout would stay
        // at 0 — altering focus behavior for every other app — until the next sign-in.
        // ProcessExit runs on CLR shutdown, including the unhandled-exception path, and
        // only fires as the process is ending, so it cannot affect Gauge's own behavior.
        // The guard's Restore is idempotent, so the Dispose + ProcessExit overlap on a
        // normal exit is harmless.
        _processExitHandler = (_, _) => { if (_manageForegroundLock) _foregroundLock.Restore(); };
        AppDomain.CurrentDomain.ProcessExit += _processExitHandler;

        _thresholdAlertsItem = BuildNotificationKindItem(
            "Settings_NotifyThresholds", UsageNotificationKind.Threshold);
        _resetAlertsItem = BuildNotificationKindItem(
            "Settings_NotifyResets", UsageNotificationKind.Reset);
        // Mark the defaults now: an unmarked item reads as "off", so the menu must never be
        // blank in the window between construction and the owner's first reflect-back.
        UpdateNotificationIndicators();

        _startOnBootItem = new MenuFlyoutItem
        {
            Text = Loc.Get("Tray_StartOnBoot"),
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
            ToolTipText = "AgentGauge",
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

    /// <summary>Reflects the current desired start-on-boot state in the menu indicator.</summary>
    public void SetStartOnBootChecked(bool isChecked)
    {
        _startOnBoot = isChecked;
        UpdateStartOnBootIndicator();
    }

    /// <summary>
    /// Reflects the real notification preferences in the menu indicators. Called at startup
    /// and after every apply — from either surface — so the menu and the settings card can
    /// never show different answers.
    /// </summary>
    public void SetNotificationPreferences(NotificationPreferences preferences)
    {
        _notifications = preferences;
        UpdateNotificationIndicators();
    }

    /// <summary>
    /// One menu item per alert kind. The item flips its own indicator optimistically and
    /// raises intent; the owner's reflect-back corrects it if the write did not take.
    /// </summary>
    private MenuFlyoutItem BuildNotificationKindItem(string labelKey, UsageNotificationKind kind)
    {
        var item = new MenuFlyoutItem
        {
            Text = Loc.Get(labelKey),
            MinWidth = ItemMinWidth,
            Padding = new Thickness(11, 8, 11, 8),
        };
        item.Click += (_, _) =>
        {
            var enabled = !_notifications.Allows(kind);
            _notifications = _notifications.With(kind, enabled);
            UpdateNotificationIndicators();
            NotificationKindToggled?.Invoke(this, (kind, enabled));
        };
        return item;
    }

    /// <summary>
    /// Updates the tooltip with a last-updated time and a short usage summary.
    /// Shell tooltips are length-limited (~127 chars), so keep <paramref name="summary"/> brief.
    /// </summary>
    public void UpdateToolTip(string summary, DateTimeOffset lastUpdated, string statusSummary = "")
    {
        var text = Loc.Format("Tray_Tooltip", summary, lastUpdated.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture));
        // Put incidents first so the shell's 127-character limit cannot hide them behind usage.
        if (statusSummary.Length > 0) text = statusSummary + "\n" + text;
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
        if (_ring is null && LoadThemedIcon() is { } icon)
        {
            UpdateIcon(icon);
        }
    }

    /// <summary>
    /// Shows the chosen tool's logo ring, or — for null — returns to the classic gauge that
    /// <see cref="UpdateUsageLevel"/> drives. Redraws only when what the icon shows changed.
    /// </summary>
    public void UpdateStatusRing(StatusRingState? ring, UsageDisplayBasis basis)
    {
        var wasRing = _ring is not null;
        _ring = ring;
        _ringBasis = basis;
        if (ring is null)
        {
            _ringDrawn = null;
            if (wasRing && LoadThemedIcon() is { } icon) UpdateIcon(icon);
            return;
        }

        RefreshRingIcon();
    }

    private void RefreshRingIcon()
    {
        if (_ring is null) return;
        var px = TrayIconPixelSize();
        var dark = IsTaskbarDarkTheme();
        var inputs = (_ring, _ringBasis, px, dark);
        if (_ringDrawn == inputs) return;
        try
        {
            UpdateIcon(_ringRenderer.Render(_ring, px, dark, _ringBasis));
            _ringDrawn = inputs;
        }
        catch (Exception ex) when (ex is ArgumentException or ExternalException or InvalidOperationException)
        {
            DiagnosticsLog.Write("tray", $"Tray ring icon could not be drawn: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// The tray's small-icon size at the taskbar's DPI. Read per draw, because the ring is
    /// rendered at exact pixels and a DPI change would otherwise leave it scaled.
    /// </summary>
    private static int TrayIconPixelSize()
    {
        var taskbar = NativeMethods.FindWindow("Shell_TrayWnd", null);
        var dpi = taskbar != IntPtr.Zero ? NativeMethods.GetDpiForWindow(taskbar) : 0;
        if (dpi == 0) dpi = NativeMethods.GetDpiForSystem();
        var size = NativeMethods.GetSystemMetricsForDpi(NativeMethods.SM_CXSMICON, dpi);
        return size > 0 ? size : 16;
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
        if (previous is not null && !ReferenceEquals(previous, icon) && !_iconCache.ContainsValue(previous))
        {
            previous?.Dispose();
        }
    }

    private void UpdateStartOnBootIndicator()
    {
        // Right-aligned secondary text; empty when off so nothing shows on the left.
        _startOnBootItem.KeyboardAcceleratorTextOverride = _startOnBoot ? CheckedGlyph : string.Empty;
    }

    private void UpdateNotificationIndicators()
    {
        _thresholdAlertsItem.KeyboardAcceleratorTextOverride =
            _notifications.Thresholds ? CheckedGlyph : string.Empty;
        _resetAlertsItem.KeyboardAcceleratorTextOverride =
            _notifications.Resets ? CheckedGlyph : string.Empty;
    }

    private void ScheduleContextMenuReposition()
    {
        _dispatcher?.TryEnqueue(DispatcherQueuePriority.Low, RepositionContextMenuAboveTray);
    }

    private void RepositionContextMenuAboveTray()
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

        var width = rect.right - rect.left;
        var height = rect.bottom - rect.top;
        var grow = 0;
        if (!_contextMenuMeasured)
        {
            _contextMenuMeasured = true;
            var scale = NativeMethods.GetDpiForWindow(hwnd) / 96.0;
            if (scale > 1) grow = (int)Math.Round(FirstOpenUnscaledDip * (scale - 1));
        }

        var work = info.rcWork;
        var (left, top) = Views.PopoverPlacement.MenuAboveTray(
            work.left, work.right, work.bottom,
            width, height + grow,
            cursor.X, MenuBottomMargin, MenuSideMargin);

        _ = NativeMethods.SetWindowPos(
            hwnd, IntPtr.Zero, left, top, width, height + grow,
            (grow == 0 ? NativeMethods.SWP_NOSIZE : 0) | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
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

            if (_ring is not null)
            {
                RefreshRingIcon();
            }
            else if (LoadThemedIcon() is { } icon)
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
            if (_iconCache.TryGetValue(path, out var cached)) return cached;
            if (File.Exists(path))
            {
                var icon = new Icon(path);
                _iconCache.Add(path, icon);
                return icon;
            }
        }

        DiagnosticsLog.Write("tray", $"Tray icon asset not found for stem '{stem}{_levelSuffix}'.");
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
            Text = Loc.Get("Tray_Exit"),
            MinWidth = ItemMinWidth,
            Padding = new Thickness(11, 8, 11, 8),
        };
        exit.Click += (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty);

        var menu = new MenuFlyout();
        menu.Items.Add(_thresholdAlertsItem);
        menu.Items.Add(_resetAlertsItem);
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(_startOnBootItem);
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(exit);
        return menu;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;

        _uiSettings.ColorValuesChanged -= OnColorValuesChanged;
        // Restore before unhooking, not after: a restore that the system refuses keeps its
        // baseline, and leaving the handler subscribed until it has succeeded is what gives
        // that one more attempt as the process ends. A restore that did succeed leaves no
        // baseline, so the handler's later call is a no-op.
        if (_manageForegroundLock) _foregroundLock.Restore();
        AppDomain.CurrentDomain.ProcessExit -= _processExitHandler;
        _trayIcon.Dispose();
        if (_currentIcon is not null && !_iconCache.ContainsValue(_currentIcon)) _currentIcon.Dispose();
        foreach (var icon in _iconCache.Values) icon.Dispose();
        _iconCache.Clear();
        _ringRenderer.Dispose();
        _currentIcon = null;
    }

    /// <summary>
    /// Re-persists the foreground-lock baseline after settings.json had to be replaced. Once
    /// captured the guard keeps it only in memory, so a replacement silently drops the one
    /// value a later hard kill would need — see
    /// <see cref="ForegroundLockGuard.RepersistBaseline"/>.
    /// </summary>
    public void RepersistForegroundLockBaseline()
    {
        if (_manageForegroundLock) _foregroundLock.RepersistBaseline();
    }

    private static class NativeMethods
    {
        public const uint MONITOR_DEFAULTTONEAREST = 0x2;
        public const int SM_CXSMICON = 49;

        [DllImport("user32.dll", EntryPoint = "FindWindowW", CharSet = CharSet.Unicode)]
        public static extern IntPtr FindWindow(string lpClassName, string? lpWindowName);

        [DllImport("user32.dll")]
        public static extern uint GetDpiForWindow(IntPtr hwnd);

        [DllImport("user32.dll")]
        public static extern uint GetDpiForSystem();

        [DllImport("user32.dll")]
        public static extern int GetSystemMetricsForDpi(int nIndex, uint dpi);
        public const uint SWP_NOSIZE = 0x0001;
        public const uint SWP_NOZORDER = 0x0004;
        public const uint SWP_NOACTIVATE = 0x0010;

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
