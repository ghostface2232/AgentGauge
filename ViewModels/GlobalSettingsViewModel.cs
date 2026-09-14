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

    public GlobalSettingsViewModel(
        NotificationPreferences notifications,
        bool startOnBoot,
        UsageViewMode viewMode,
        UsageDisplayBasis displayBasis = UsageDisplayBasis.Used,
        bool showSparkline = true)
    {
        SyncFromSystem(notifications, startOnBoot);
        _suspendSideEffects = true;
        ViewModeIndex = (int)viewMode;
        DisplayBasisIndex = (int)displayBasis;
        ShowSparkline = showSparkline;
        LanguageIndex = (int)Loc.Current;
        _suspendSideEffects = false;
    }

    /// <summary>Raised when the user flips a per-kind notification toggle (not on a programmatic sync).</summary>
    public event EventHandler<(UsageNotificationKind Kind, bool Enabled)>? NotificationKindToggleRequested;

    /// <summary>Raised when the user flips the start-on-boot toggle (not on a programmatic sync).</summary>
    public event EventHandler<bool>? StartOnBootToggleRequested;

    /// <summary>Raised when the user picks a different card view mode (not on a programmatic sync).</summary>
    public event EventHandler<UsageViewMode>? ViewModeChangeRequested;

    /// <summary>Raised when the user picks a different percent basis (not on a programmatic sync).</summary>
    public event EventHandler<UsageDisplayBasis>? DisplayBasisChangeRequested;

    /// <summary>Raised when the user flips the sparkline toggle (not on a programmatic sync).</summary>
    public event EventHandler<bool>? SparklineToggleRequested;

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
    /// Selected percent basis as a ComboBox index — 0 = Used, 1 = Remaining — matching
    /// <see cref="DisplayBasisOptions"/> and the <see cref="UsageDisplayBasis"/> enum's values.
    /// </summary>
    [ObservableProperty] public partial int DisplayBasisIndex { get; set; }

    /// <summary>Localized labels for the basis dropdown, in <see cref="UsageDisplayBasis"/> order.</summary>
    public IReadOnlyList<string> DisplayBasisOptions { get; } =
        [Loc.Get("DisplayBasis_Used"), Loc.Get("DisplayBasis_Remaining")];

    /// <summary>
    /// Whether bar rows show the burndown sparkline. It has no live service behind it, but
    /// like every other setting here it is reconciled against the persisted result, so a
    /// write that settings.json refused snaps the switch back instead of showing a state
    /// the next launch would not honour.
    /// </summary>
    [ObservableProperty] public partial bool ShowSparkline { get; set; }

    /// <summary>
    /// Selected UI language as a ComboBox index matching the <see cref="AppLanguage"/> enum
    /// values. Choosing a different language persists it and relaunches the app.
    /// </summary>
    [ObservableProperty] public partial int LanguageIndex { get; set; }

    /// <summary>
    /// What the card's notice row says about settings.json, or null for no notice. Two
    /// things need saying and neither can be read off the switches: a write the file refused
    /// (every reflect-back above is otherwise silent — the switch just snaps back, which
    /// reads as a bug rather than as the disk saying no), and a document replaced because it
    /// had stopped being JSON. <c>App</c> sets this from what actually happened on disk; it
    /// is not a state the user can pick, so unlike the settings around it, it raises no
    /// intent event.
    /// </summary>
    [ObservableProperty] public partial string? SettingsNotice { get; set; }

    /// <summary>Whether <see cref="SettingsNotice"/> has anything to show.</summary>
    public bool HasSettingsNotice => SettingsNotice is { Length: > 0 };

    partial void OnSettingsNoticeChanged(string? value) => OnPropertyChanged(nameof(HasSettingsNotice));

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

    partial void OnDisplayBasisIndexChanged(int value)
    {
        if (_suspendSideEffects) return;
        DisplayBasisChangeRequested?.Invoke(this,
            value == (int)UsageDisplayBasis.Remaining ? UsageDisplayBasis.Remaining : UsageDisplayBasis.Used);
    }

    partial void OnShowSparklineChanged(bool value)
    {
        if (_suspendSideEffects) return;
        SparklineToggleRequested?.Invoke(this, value);
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

    /// <summary>
    /// Reflects the persisted view mode without raising a new request. Used after a save
    /// that failed, so the dropdown snaps back to the mode that is actually on disk instead
    /// of showing a choice the next launch would not honour.
    /// </summary>
    public void SetViewMode(UsageViewMode mode)
    {
        _suspendSideEffects = true;
        ViewModeIndex = (int)mode;
        _suspendSideEffects = false;
    }

    /// <summary>Reflects the persisted percent basis without raising a new request.</summary>
    public void SetDisplayBasis(UsageDisplayBasis basis)
    {
        _suspendSideEffects = true;
        DisplayBasisIndex = (int)basis;
        _suspendSideEffects = false;
    }

    /// <summary>Reflects the persisted sparkline state without raising a new request.</summary>
    public void SetShowSparkline(bool show)
    {
        _suspendSideEffects = true;
        ShowSparkline = show;
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
