using System.Diagnostics;
using System.Drawing;
using System.IO;
using CommunityToolkit.Mvvm.Input;
using H.NotifyIcon;
using Microsoft.UI.Xaml.Controls;

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
/// </summary>
public sealed class TrayIconService : IDisposable
{
    private readonly TaskbarIcon _trayIcon;
    private readonly ToggleMenuFlyoutItem _startOnBootItem;

    // Held so we can dispose the previous GDI icon handle when swapping icons.
    private Icon? _currentIcon;
    private bool _disposed;

    /// <summary>Raised on left-click. Next step wires this to the popover toggle.</summary>
    public event EventHandler? LeftClicked;

    /// <summary>Context menu: "새로고침".</summary>
    public event EventHandler? RefreshRequested;

    /// <summary>Context menu: "시작프로그램 등록" toggled. Argument is the new desired state.</summary>
    public event EventHandler<bool>? StartOnBootToggled;

    /// <summary>Context menu: "종료".</summary>
    public event EventHandler? ExitRequested;

    public TrayIconService()
    {
        _startOnBootItem = new ToggleMenuFlyoutItem { Text = "시작프로그램 등록" };
        _startOnBootItem.Click += (_, _) =>
            StartOnBootToggled?.Invoke(this, _startOnBootItem.IsChecked);

        _trayIcon = new TaskbarIcon
        {
            ToolTipText = "Gauge",
            // Fire left-click immediately instead of waiting out the double-click
            // interval — a single click should toggle the popover with no lag.
            NoLeftClickDelay = true,
            // SecondWindow hosts the WinUI MenuFlyout in its own popup window,
            // which works for a tray-only app with no main window visible.
            // (Fallback if it ever misbehaves: ContextMenuMode.PopupMenu = native menu.)
            ContextMenuMode = ContextMenuMode.SecondWindow,
            LeftClickCommand = new RelayCommand(() => LeftClicked?.Invoke(this, EventArgs.Empty)),
            ContextFlyout = BuildContextMenu(),
        };

        LoadInitialIcon();

        // Create the Shell_NotifyIcon entry now; the icon is not in a visual tree.
        _trayIcon.ForceCreate(enablesEfficiencyMode: false);
    }

    /// <summary>
    /// Reflects the current desired start-on-boot state in the menu checkmark.
    /// Wiring to actual startup registration comes in a later step.
    /// </summary>
    public void SetStartOnBootChecked(bool isChecked)
    {
        _startOnBootItem.IsChecked = isChecked;
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

    private void LoadInitialIcon()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Assets", "gauge_icon.ico");
        if (File.Exists(path))
        {
            _currentIcon = new Icon(path);
            _trayIcon.Icon = _currentIcon;
        }
        else
        {
            // Not fatal: the icon can still be set later via UpdateIcon.
            Debug.WriteLine($"[Gauge] Tray icon asset not found at {path}");
        }
    }

    private MenuFlyout BuildContextMenu()
    {
        var refresh = new MenuFlyoutItem { Text = "새로고침" };
        refresh.Click += (_, _) => RefreshRequested?.Invoke(this, EventArgs.Empty);

        var exit = new MenuFlyoutItem { Text = "종료" };
        exit.Click += (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty);

        var menu = new MenuFlyout();
        menu.Items.Add(refresh);
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

        _trayIcon.Dispose();
        _currentIcon?.Dispose();
        _currentIcon = null;
    }
}
