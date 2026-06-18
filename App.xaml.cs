using System.Diagnostics;
using Gauge.Models;
using Gauge.Providers;
using Gauge.Services;
using Gauge.ViewModels;
using Gauge.Views;
using Microsoft.UI.Dispatching;
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
    private PopoverWindow? _popover;
    private TrayIconService? _trayIcon;
    private UsageCoordinator? _coordinator;
    private UsageViewModel? _viewModel;

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
        _popover = new PopoverWindow();

        _trayIcon = new TrayIconService();
        _trayIcon.LeftClicked += OnTrayLeftClicked;
        _trayIcon.StartOnBootToggled += OnTrayStartOnBootToggled;
        _trayIcon.ExitRequested += OnTrayExitRequested;

        // Data pipeline: providers → UsageService (parallel + isolated) → coordinator
        // (60s timer + cache + debounced forced refresh) → view model → UI/tray.
        var ccusage = new CcusageClient(new ProcessRunner());
        var usageService = new UsageService(new IUsageProvider[]
        {
            new ClaudeProvider(ccusage),
            new CodexProvider(ccusage),
        });

        _viewModel = new UsageViewModel();
        _viewModel.RefreshRequested += OnManualRefreshRequested;
        _popover.BindViewModel(_viewModel);

        _coordinator = new UsageCoordinator(usageService, DispatcherQueue.GetForCurrentThread());
        _coordinator.Updated += OnUsageUpdated;

        // A confirmed popover open triggers a (debounced) forced refresh. Routing it
        // through Opened — not the click — keeps the toggle guard and the refresh
        // debounce from interfering: a click that closes the popover never refreshes.
        _popover.Opened += OnPopoverOpened;

        _coordinator.Start();
    }

    private void OnUsageUpdated(object? sender, UsageState state)
    {
        // Coordinator marshals this to the UI thread.
        _viewModel?.Apply(state);
        if (_viewModel is not null)
        {
            _trayIcon?.UpdateToolTip(_viewModel.TrayTooltipSummary, _viewModel.LastUpdatedAt ?? DateTimeOffset.Now);
        }
    }

    private async void OnPopoverOpened(object? sender, EventArgs e)
    {
        if (_coordinator is not null)
        {
            await _coordinator.ForceRefreshAsync();
        }
    }

    private async void OnManualRefreshRequested(object? sender, EventArgs e)
    {
        // Routed through the same debounced path so the data source isn't hammered;
        // within 10s of a refresh it just re-shows the cached value.
        if (_coordinator is not null)
        {
            await _coordinator.ForceRefreshAsync();
        }
    }

    private void OnTrayLeftClicked(object? sender, EventArgs e)
    {
        // The toggle guard inside PopoverWindow turns a click-while-open into a close.
        _popover?.Toggle();
    }

    private void OnTrayStartOnBootToggled(object? sender, bool enabled)
    {
        // TODO(later): register/unregister run-on-startup.
        Debug.WriteLine($"[Gauge] Tray menu → start-on-boot toggled to {enabled} (not wired yet)");
    }

    private void OnTrayExitRequested(object? sender, EventArgs e)
    {
        _coordinator?.Dispose();
        _coordinator = null;
        _trayIcon?.Dispose();
        _trayIcon = null;
        Exit();
    }
}
