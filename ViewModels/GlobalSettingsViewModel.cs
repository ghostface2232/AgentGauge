using CommunityToolkit.Mvvm.ComponentModel;
using Gauge.Localization;
using Gauge.Models;

namespace Gauge.ViewModels;

/// <summary>
/// App-wide settings shown at the top of the settings panel: the per-kind notification
/// toggles and run-on-startup. Every one of these states is owned elsewhere — start-on-boot
/// lives in the registry Run key, the notification kinds gate the live
/// <c>UsageNotificationService</c> and persist to settings.json — and every one is also
/// surfaced in the tray menu, so this view model only holds the toggle state and emits
/// intent. The owner (<c>App</c>) applies the change against the real service, then
/// reconciles both surfaces via <see cref="SetStartOnBoot"/> /
/// <see cref="SyncNotifications"/> / <see cref="SyncFromSystem"/>. Those setters suspend
/// the change events so reflecting an external state never loops back as a new request.
/// </summary>
public sealed partial class GlobalSettingsViewModel : ObservableObject
{
    private bool _suspendSideEffects;

    public GlobalSettingsViewModel(NotificationPreferences notifications, bool startOnBoot, UsageViewMode viewMode)
    {
        SyncFromSystem(notifications, startOnBoot);
        _suspendSideEffects = true;
        ViewModeIndex = (int)viewMode;
        LanguageIndex = (int)Loc.Current;
        _suspendSideEffects = false;
    }

    /// <summary>Raised when the user flips a per-kind notification toggle (not on a programmatic sync).</summary>
    public event EventHandler<(UsageNotificationKind Kind, bool Enabled)>? NotificationKindToggleRequested;

    /// <summary>Raised when the user flips the start-on-boot toggle (not on a programmatic sync).</summary>
    public event EventHandler<bool>? StartOnBootToggleRequested;

    /// <summary>Raised when the user picks a different card view mode (not on a programmatic sync).</summary>
    public event EventHandler<UsageViewMode>? ViewModeChangeRequested;

    /// <summary>Raised when the user picks a different UI language (not on a programmatic sync).</summary>
    public event EventHandler<AppLanguage>? LanguageChangeRequested;

    /// <summary>
    /// Per-kind alert toggles. These are the whole of the notification setting — there is no
    /// separate master switch — and the tray menu shows the same two, so either surface's
    /// change is reflected onto the other.
    /// </summary>
    [ObservableProperty] public partial bool NotifyThresholds { get; set; }
    [ObservableProperty] public partial bool NotifyResets { get; set; }

    [ObservableProperty] public partial bool StartOnBoot { get; set; }

    /// <summary>
    /// Selected card view mode as a ComboBox index — 0 = Bar, 1 = Gauge — matching
    /// <see cref="ViewModeOptions"/> and the <see cref="UsageViewMode"/> enum's values.
    /// </summary>
    [ObservableProperty] public partial int ViewModeIndex { get; set; }

    /// <summary>Localized labels for the view-mode dropdown, in <see cref="UsageViewMode"/> order.</summary>
    public IReadOnlyList<string> ViewModeOptions { get; } =
        [Loc.Get("ViewMode_Bar"), Loc.Get("ViewMode_Gauge")];

    /// <summary>
    /// Selected UI language as a ComboBox index matching the <see cref="AppLanguage"/> enum
    /// values. Choosing a different language persists it and relaunches the app.
    /// </summary>
    [ObservableProperty] public partial int LanguageIndex { get; set; }

    /// <summary>
    /// Language names in <see cref="AppLanguage"/> order, each in its own language (the
    /// standard convention for language pickers), so no localization column is needed.
    /// </summary>
    public IReadOnlyList<string> LanguageOptions { get; } = ["한국어", "English", "日本語"];

    partial void OnViewModeIndexChanged(int value)
    {
        if (_suspendSideEffects) return;
        ViewModeChangeRequested?.Invoke(this, value == (int)UsageViewMode.Gauge ? UsageViewMode.Gauge : UsageViewMode.Bar);
    }

    partial void OnLanguageIndexChanged(int value)
    {
        if (_suspendSideEffects) return;
        if (value is >= 0 and <= (int)AppLanguage.Japanese)
        {
            LanguageChangeRequested?.Invoke(this, (AppLanguage)value);
        }
    }

    /// <summary>Reflects the persisted language after a failed change without raising a new request.</summary>
    public void SetLanguage(AppLanguage language)
    {
        _suspendSideEffects = true;
        LanguageIndex = (int)language;
        _suspendSideEffects = false;
    }

    partial void OnNotifyThresholdsChanged(bool value)
    {
        if (_suspendSideEffects) return;
        NotificationKindToggleRequested?.Invoke(this, (UsageNotificationKind.Threshold, value));
    }

    partial void OnNotifyResetsChanged(bool value)
    {
        if (_suspendSideEffects) return;
        NotificationKindToggleRequested?.Invoke(this, (UsageNotificationKind.Reset, value));
    }

    partial void OnStartOnBootChanged(bool value)
    {
        if (_suspendSideEffects) return;
        StartOnBootToggleRequested?.Invoke(this, value);
    }

    /// <summary>
    /// Reflects the real notification preferences without raising the toggle events — used
    /// after every apply, including one made from the tray menu while the panel is open.
    /// </summary>
    public void SyncNotifications(NotificationPreferences preferences)
    {
        _suspendSideEffects = true;
        NotifyThresholds = preferences.Thresholds;
        NotifyResets = preferences.Resets;
        _suspendSideEffects = false;
    }

    /// <summary>Reflects the real start-on-boot state without raising the toggle event.</summary>
    public void SetStartOnBoot(bool value)
    {
        _suspendSideEffects = true;
        StartOnBoot = value;
        _suspendSideEffects = false;
    }

    /// <summary>
    /// Reflects the real state of the toggles without raising their events — used at
    /// construction and whenever the settings panel reopens, so a change made elsewhere
    /// (e.g. the tray menu flipping start-on-boot) shows up correctly.
    /// </summary>
    public void SyncFromSystem(NotificationPreferences notifications, bool startOnBoot)
    {
        _suspendSideEffects = true;
        NotifyThresholds = notifications.Thresholds;
        NotifyResets = notifications.Resets;
        StartOnBoot = startOnBoot;
        _suspendSideEffects = false;
    }
}
