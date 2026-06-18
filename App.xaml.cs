using System.Diagnostics;
using Gauge.Services;
using Gauge.Views;
using Microsoft.UI.Xaml;

namespace Gauge;

/// <summary>
/// Application root. Gauge is a tray-only background app, so the main window is
/// created but never activated at startup — nothing is shown to the user yet.
/// The tray icon owns all user interaction for now.
/// </summary>
public partial class App : Application
{
    // Held so they are not garbage-collected while the app runs.
    private MainWindow? _mainWindow;
    private TrayIconService? _trayIcon;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // Create the window but deliberately do NOT call Activate(): a WinUI
        // window stays hidden until activated, which is exactly the tray-only
        // background behavior we want at startup.
        _mainWindow = new MainWindow();

        _trayIcon = new TrayIconService();
        _trayIcon.LeftClicked += OnTrayLeftClicked;
        _trayIcon.RefreshRequested += OnTrayRefreshRequested;
        _trayIcon.StartOnBootToggled += OnTrayStartOnBootToggled;
        _trayIcon.ExitRequested += OnTrayExitRequested;
    }

    private void OnTrayLeftClicked(object? sender, EventArgs e)
    {
        // TODO(next step): toggle the popover window here.
        Debug.WriteLine("[Gauge] Tray left-click → toggle popover (not wired yet)");
    }

    private void OnTrayRefreshRequested(object? sender, EventArgs e)
    {
        // TODO(later): trigger a forced provider refresh.
        Debug.WriteLine("[Gauge] Tray menu → refresh (not wired yet)");
    }

    private void OnTrayStartOnBootToggled(object? sender, bool enabled)
    {
        // TODO(later): register/unregister run-on-startup.
        Debug.WriteLine($"[Gauge] Tray menu → start-on-boot toggled to {enabled} (not wired yet)");
    }

    private void OnTrayExitRequested(object? sender, EventArgs e)
    {
        _trayIcon?.Dispose();
        _trayIcon = null;
        Exit();
    }
}
