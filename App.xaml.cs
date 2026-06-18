using System.Net.Http;
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
    private PopoverWindow? _popover;
    private TrayIconService? _trayIcon;
    private UsageCoordinator? _coordinator;
    private UsageViewModel? _viewModel;
    private SettingsViewModel? _settingsViewModel;
    private IReadOnlyDictionary<ToolKind, IAuthenticationProvider>? _authentication;
    private StartupService? _startupService;
    private HttpClient? _httpClient;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // Create the window but deliberately do NOT call Activate(): a WinUI
        // window stays hidden until activated, which is exactly the tray-only
        // background behavior we want at startup.
        _popover = new PopoverWindow();
        _popover.SettingsOpened += OnSettingsOpened;

        _trayIcon = new TrayIconService();
        _trayIcon.LeftClicked += OnTrayLeftClicked;
        _trayIcon.StartOnBootToggled += OnTrayStartOnBootToggled;
        _trayIcon.ExitRequested += OnTrayExitRequested;

        // Reflect the real run-on-startup state in the tray menu checkmark.
        _startupService = new StartupService();
        _trayIcon.SetStartOnBootChecked(_startupService.IsEnabled());

        // Data pipeline: providers → UsageService (parallel + isolated) → coordinator
        // (60s timer + cache + debounced forced refresh) → view model → UI/tray.
        // Providers read each tool's real usage from its official OAuth usage API,
        // using the token the CLI already stores locally (read-only).
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        var cliCredentials = new CliCredentialSource();
        var credentials = new CredentialSourceChain(new[] { cliCredentials });
        var locator = new CliLocator();
        var processRunner = new CliProcessRunner();
        var authentication = new IAuthenticationProvider[]
        {
            new CliAuthenticationProvider(ToolKind.ClaudeCode, cliCredentials, locator, processRunner),
            new CliAuthenticationProvider(ToolKind.Codex, cliCredentials, locator, processRunner),
        };
        _authentication = authentication.ToDictionary(provider => provider.Tool);

        var usageService = new UsageService(new IUsageProvider[]
        {
            new ClaudeProvider(_httpClient, credentials),
            new CodexProvider(_httpClient, credentials),
        });

        _settingsViewModel = new SettingsViewModel(authentication);
        _settingsViewModel.AuthenticationSucceeded += OnAuthenticationSucceeded;
        _popover.BindSettingsViewModel(_settingsViewModel);
        _ = _settingsViewModel.RefreshAsync();

        _viewModel = new UsageViewModel();
        _viewModel.RefreshRequested += OnManualRefreshRequested;
        _popover.BindViewModel(_viewModel);

        _coordinator = new UsageCoordinator(usageService, DispatcherQueue.GetForCurrentThread());
        _coordinator.Updated += OnUsageUpdated;
        _coordinator.AuthenticationRequired += OnAuthenticationRequired;

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
            // Recolor the tray icon by the highest usage ratio (≥70% caution, ≥90% danger).
            _trayIcon?.UpdateUsageLevel(_viewModel.HighestUsageRatio);
        }
    }

    private async void OnPopoverOpened(object? sender, EventArgs e)
    {
        if (_coordinator is not null)
        {
            await _coordinator.RefreshAsync(RefreshReason.PopoverOpened);
        }
    }

    private async void OnManualRefreshRequested(object? sender, EventArgs e)
    {
        // Routed through the same debounced path so the data source isn't hammered;
        // within 10s of a refresh it just re-shows the cached value.
        if (_coordinator is not null)
        {
            await _coordinator.RefreshAsync(RefreshReason.Manual);
        }
    }

    private async void OnAuthenticationSucceeded(object? sender, EventArgs e)
    {
        if (_coordinator is not null)
        {
            await _coordinator.RefreshAsync(RefreshReason.AuthenticationChanged);
        }
    }

    private void OnSettingsOpened(object? sender, EventArgs e)
    {
        if (_settingsViewModel is not null) _ = _settingsViewModel.RefreshAsync();
    }

    private void OnAuthenticationRequired(object? sender, ToolKind tool)
    {
        if (_authentication?.TryGetValue(tool, out var provider) == true)
        {
            provider.ReportInvalidCredentials();
        }
    }

    private void OnTrayLeftClicked(object? sender, EventArgs e)
    {
        // The toggle guard inside PopoverWindow turns a click-while-open into a close.
        _popover?.Toggle();
    }

    private void OnTrayStartOnBootToggled(object? sender, bool enabled)
    {
        // Apply, then sync the menu checkmark to the actual registry state (so a
        // failed write reverts the check instead of lying).
        var actual = _startupService?.SetEnabled(enabled) ?? false;
        _trayIcon?.SetStartOnBootChecked(actual);
    }

    private void OnTrayExitRequested(object? sender, EventArgs e)
    {
        // Stop the timer and cancel any in-flight usage calls first, then
        // remove the tray icon (which also restores the foreground-lock setting and
        // unsubscribes the theme listener), then quit.
        _coordinator?.Dispose();
        _coordinator = null;
        _trayIcon?.Dispose();
        _trayIcon = null;
        _httpClient?.Dispose();
        _httpClient = null;
        Exit();
    }
}
