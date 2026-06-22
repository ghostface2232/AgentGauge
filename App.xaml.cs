using System.Net.Http;
using Gauge.Localization;
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
    private UsageNotificationService? _notificationService;
    private UsageViewModel? _viewModel;
    private SettingsViewModel? _settingsViewModel;
    private IReadOnlyDictionary<ToolKind, IAuthenticationProvider>? _authentication;
    private ToolRegistry? _toolRegistry;
    private StartupService? _startupService;
    private NotificationSettingsStore? _notificationSettingsStore;
    private ViewModeSettingsStore? _viewModeSettingsStore;
    private UpdateService? _updateService;
    private HttpClient? _httpClient;
    private AntigravityProvider? _antigravityProvider;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // Resolve the UI language before any window or the tray menu is built, so every
        // surface is created in the right language. First run detects it from the OS
        // display language and persists the choice to settings.json.
        Loc.Initialize(LanguageService.InitializeFromSettings());

        // Create the window but deliberately do NOT call Activate(): a WinUI
        // window stays hidden until activated, which is exactly the tray-only
        // background behavior we want at startup.
        _popover = new PopoverWindow();
        _popover.SettingsOpened += OnSettingsOpened;

        _trayIcon = new TrayIconService();
        _trayIcon.LeftClicked += OnTrayLeftClicked;
        _trayIcon.StartOnBootToggled += OnTrayStartOnBootToggled;
        _trayIcon.NotificationsToggled += OnTrayNotificationsToggled;
        _trayIcon.ExitRequested += OnTrayExitRequested;

        // Reflect the real run-on-startup state in the tray menu checkmark.
        _startupService = new StartupService();
        _trayIcon.SetStartOnBootChecked(_startupService.IsEnabled());

        // Data pipeline: providers → UsageService (parallel + isolated) → coordinator
        // (60s timer + cache + debounced forced refresh) → view model → UI/tray.
        // Providers read each tool's real usage from its official OAuth usage API,
        // using the token the CLI already stores locally (read-only).
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        // Which tools the user has registered ("+"/remove in settings). First run
        // defaults to Claude Code + Codex; persisted to %APPDATA%\Gauge\settings.json.
        _toolRegistry = new ToolRegistry(new ToolRegistryStore());
        var cliCredentials = new CliCredentialSource();
        var cursorCredentials = new CursorCredentialSource();
        var credentials = new CredentialSourceChain(new ICredentialSource[] { cliCredentials, cursorCredentials });
        var locator = new CliLocator();
        var processRunner = new CliProcessRunner();
        // Let the providers recover an expired local token after boot by nudging the official
        // CLI to refresh its own token, instead of failing until the tool is opened. Each uses
        // a command verified to go through the CLI's authenticated bootstrap (which performs
        // the refresh) WITHOUT spending model usage — a status command would only print cached
        // auth and never refresh:
        //   • Claude: `claude mcp list` (~5s). `claude auth status` is a no-op refresh.
        //   • Codex:  `codex doctor` (~3s, runs local health checks + a reachability probe).
        //     `codex login status` / `codex mcp list` never touch the ChatGPT token. The Codex
        //     token is a ~10-day JWT, so this rarely fires; the 30s timeout is generous for
        //     slow networks / low-spec PCs (the refresh completes early, so even a timeout
        //     leaves the freshened token on disk).
        var claudeTokenRefresher = new DelegatedTokenRefresher(
            "claude", "mcp list", TimeSpan.FromSeconds(15), locator, processRunner);
        var codexTokenRefresher = new DelegatedTokenRefresher(
            "codex", "doctor", TimeSpan.FromSeconds(30), locator, processRunner);
        // Read auth state through the full credential chain (not just the CLI source)
        // so non-CLI tools like Cursor report their real logged-in state on the card.
        // Antigravity's sign-in is owned by the IDE's OAuth with no Gauge-readable credential,
        // so its card state can't come from the credential chain like the CLI tools' do.
        var authentication = ToolCatalog.All
            .Select(descriptor => descriptor.Kind == ToolKind.Antigravity
                ? (IAuthenticationProvider)new AntigravityAuthenticationProvider()
                : new CliAuthenticationProvider(descriptor.Kind, credentials, locator, processRunner))
            .ToArray();
        _authentication = authentication.ToDictionary(provider => provider.Tool);

        // Providers are built for the whole catalog but only queried for registered
        // tools (the registry filter). Adding/removing a tool needs no pipeline rebuild.
        // Antigravity has no usage endpoint or Gauge-owned credential: it reads quota from the
        // IDE's local language server, or an engine Gauge launches itself when the IDE is closed.
        // Held for disposal so that delegate engine (and its sidecars) is torn down on exit.
        _antigravityProvider = new AntigravityProvider();
        var usageService = new UsageService(
            new IUsageProvider[]
            {
                new ClaudeProvider(_httpClient, credentials, claudeTokenRefresher),
                new CodexProvider(_httpClient, credentials, codexTokenRefresher),
                new CursorProvider(_httpClient, credentials),
                _antigravityProvider,
            },
            _toolRegistry.IsEnabled);

        // Global settings toggles (notifications, run-on-startup) shown at the top of the
        // settings panel. The view model only emits intent; App owns the services and
        // applies/reconciles the change (see OnGlobal* handlers below). The initial state
        // comes from the persisted notifications flag and the real Run-key startup state.
        _notificationSettingsStore = new NotificationSettingsStore();
        var notificationsEnabled = _notificationSettingsStore.Load();
        _trayIcon.SetNotificationsChecked(notificationsEnabled);
        _viewModeSettingsStore = new ViewModeSettingsStore();
        var viewMode = _viewModeSettingsStore.Load();
        var globalSettings = new GlobalSettingsViewModel(notificationsEnabled, _startupService.IsEnabled(), viewMode);
        globalSettings.NotificationsToggleRequested += OnGlobalNotificationsToggled;
        globalSettings.StartOnBootToggleRequested += OnGlobalStartOnBootToggled;
        globalSettings.ViewModeChangeRequested += OnGlobalViewModeChanged;

        _updateService = new UpdateService();
        _settingsViewModel = new SettingsViewModel(_toolRegistry, _authentication, _updateService, globalSettings);
        _settingsViewModel.AuthenticationSucceeded += OnAuthenticationSucceeded;
        _settingsViewModel.Update.ExitRequested += OnUpdateExitRequested;
        _popover.BindSettingsViewModel(_settingsViewModel);
        _ = _settingsViewModel.RefreshAsync();
        // Quietly check GitHub Releases on launch so the settings card can surface
        // an available update; applying it stays a deliberate one-click action.
        _ = _settingsViewModel.Update.CheckInBackgroundAsync();

        _viewModel = new UsageViewModel(_toolRegistry);
        _viewModel.SetViewMode(viewMode);
        _viewModel.RefreshRequested += OnManualRefreshRequested;
        _popover.BindViewModel(_viewModel);

        _coordinator = new UsageCoordinator(
            usageService, DispatcherQueue.GetForCurrentThread(), new UsageCacheStore());
        _notificationService = new UsageNotificationService();
        _notificationService.SetEnabled(notificationsEnabled);
        _coordinator.Updated += OnUsageUpdated;
        _coordinator.AuthenticationRequired += OnAuthenticationRequired;
        _coordinator.AuthenticationRecovered += OnAuthenticationRecovered;
        // Adding/removing a service re-fetches immediately so its card appears/disappears.
        _toolRegistry.Changed += OnToolRegistryChanged;

        // A confirmed popover open triggers a (debounced) forced refresh. Routing it
        // through Opened — not the click — keeps the toggle guard and the refresh
        // debounce from interfering: a click that closes the popover never refreshes.
        _popover.Opened += OnPopoverOpened;

        _coordinator.Start();
        // Unpackaged WinUI launches do not reliably copy ordinary EXE arguments into
        // LaunchActivatedEventArgs.Arguments. Read the actual process command line so
        // the developer visual-QA switches work when launched from PowerShell/Explorer.
        var commandLine = Environment.GetCommandLineArgs();
        if (commandLine.Contains("--notification-demo", StringComparer.OrdinalIgnoreCase))
        {
            _notificationService.ShowDemoSequence();
        }

        // Post-update relaunch: the silent installer (now fully hidden) passes --updated
        // so we open the window once. Otherwise the app would relaunch straight to the
        // tray and the user couldn't tell the update had applied.
        if (commandLine.Contains("--updated", StringComparer.OrdinalIgnoreCase))
        {
            _popover.Show();
        }
    }

    private void OnUsageUpdated(object? sender, UsageState state)
    {
        // Coordinator marshals this to the UI thread.
        _notificationService?.Process(state);
        _viewModel?.Apply(state);
        // Feed the same snapshots to the settings cards so each shows its plan label.
        _settingsViewModel?.ApplyUsage(state);
        _popover?.RefreshUsageLayout();
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

    private async void OnToolRegistryChanged(object? sender, EventArgs e)
    {
        if (_coordinator is not null)
        {
            await _coordinator.RefreshAsync(RefreshReason.ToolsChanged);
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
        if (_settingsViewModel is null) return;

        // Reflect any state changed while the panel was closed — notably start-on-boot,
        // which the tray menu can also flip — before showing the toggles.
        _settingsViewModel.Global.SyncFromSystem(
            _notificationSettingsStore?.Load() ?? true,
            _startupService?.IsEnabled() ?? false);
        _ = _settingsViewModel.RefreshAsync();
    }

    // The settings-panel toggle and the tray-menu item are two views of one flag. Each
    // toggle path applies the change and then reflects it on the *other* surface, so
    // flipping notifications in one place updates the other immediately.
    private void OnGlobalNotificationsToggled(object? sender, bool enabled)
    {
        ApplyNotificationsEnabled(enabled);
        _trayIcon?.SetNotificationsChecked(enabled);
    }

    private void OnTrayNotificationsToggled(object? sender, bool enabled)
    {
        ApplyNotificationsEnabled(enabled);
        _settingsViewModel?.Global.SetNotificationsEnabled(enabled);
    }

    private void ApplyNotificationsEnabled(bool enabled)
    {
        _notificationSettingsStore?.Save(enabled);
        _notificationService?.SetEnabled(enabled);
    }

    private void OnGlobalViewModeChanged(object? sender, UsageViewMode mode)
    {
        _viewModeSettingsStore?.Save(mode);
        _viewModel?.SetViewMode(mode);
        // Bar and gauge cards differ in height; re-measure so the popover resizes to fit
        // (a no-op while the settings view is up — returning to usage re-measures anyway).
        _popover?.RefreshUsageLayout();
    }

    private void OnGlobalStartOnBootToggled(object? sender, bool enabled)
    {
        // Apply, sync the tray checkmark, and reconcile the toggle if the write failed.
        var actual = _startupService?.SetEnabled(enabled) ?? false;
        _trayIcon?.SetStartOnBootChecked(actual);
        if (actual != enabled) _settingsViewModel?.Global.SetStartOnBoot(actual);
    }

    private void OnAuthenticationRequired(object? sender, ToolKind tool)
    {
        if (_authentication?.TryGetValue(tool, out var provider) == true)
        {
            provider.ReportInvalidCredentials();
        }
    }

    private void OnAuthenticationRecovered(object? sender, ToolKind tool)
    {
        if (_authentication?.TryGetValue(tool, out var provider) == true)
        {
            provider.ReportCredentialsAccepted();
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
        // failed write reverts the check instead of lying), and keep the settings
        // toggle in step with the change made from the tray.
        var actual = _startupService?.SetEnabled(enabled) ?? false;
        _trayIcon?.SetStartOnBootChecked(actual);
        _settingsViewModel?.Global.SetStartOnBoot(actual);
    }

    private void OnTrayExitRequested(object? sender, EventArgs e) => ShutdownAndExit();

    // The installer has launched; exit so it can replace the locked files and
    // relaunch Gauge.
    private void OnUpdateExitRequested(object? sender, EventArgs e) => ShutdownAndExit();

    private void ShutdownAndExit()
    {
        // Stop the timer and cancel any in-flight usage calls first, then
        // remove the tray icon (which also restores the foreground-lock setting and
        // unsubscribes the theme listener), then quit.
        _coordinator?.Dispose();
        _coordinator = null;
        // After the coordinator has stopped (no refresh in flight), tear down any delegate
        // engine Gauge launched, along with its sidecar tree.
        _antigravityProvider?.Dispose();
        _antigravityProvider = null;
        _notificationService?.Dispose();
        _notificationService = null;
        _trayIcon?.Dispose();
        _trayIcon = null;
        _httpClient?.Dispose();
        _httpClient = null;
        Exit();
    }
}
