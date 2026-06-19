using CommunityToolkit.Mvvm.ComponentModel;

namespace Gauge.ViewModels;

/// <summary>
/// App-wide settings shown at the top of the settings panel: toggles for usage
/// notifications and run-on-startup. Both states are owned elsewhere — start-on-boot is
/// also a tray-menu item and lives in the registry Run key, notifications gate the live
/// <c>UsageNotificationService</c> and persist to settings.json — so this view model only
/// holds the toggle state and emits intent. The owner (<c>App</c>) applies the change
/// against the real service, then reconciles the toggle back to the actual result via
/// <see cref="SetStartOnBoot"/> / <see cref="SyncFromSystem"/>. Those setters suspend the
/// change events so reflecting an external state never loops back as a new request.
/// </summary>
public sealed partial class GlobalSettingsViewModel : ObservableObject
{
    private bool _suspendSideEffects;

    public GlobalSettingsViewModel(bool notificationsEnabled, bool startOnBoot)
        => SyncFromSystem(notificationsEnabled, startOnBoot);

    /// <summary>Raised when the user flips the notifications toggle (not on a programmatic sync).</summary>
    public event EventHandler<bool>? NotificationsToggleRequested;

    /// <summary>Raised when the user flips the start-on-boot toggle (not on a programmatic sync).</summary>
    public event EventHandler<bool>? StartOnBootToggleRequested;

    [ObservableProperty] public partial bool NotificationsEnabled { get; set; }
    [ObservableProperty] public partial bool StartOnBoot { get; set; }

    partial void OnNotificationsEnabledChanged(bool value)
    {
        if (_suspendSideEffects) return;
        NotificationsToggleRequested?.Invoke(this, value);
    }

    partial void OnStartOnBootChanged(bool value)
    {
        if (_suspendSideEffects) return;
        StartOnBootToggleRequested?.Invoke(this, value);
    }

    /// <summary>Reflects the real notifications-enabled state without raising the toggle event.</summary>
    public void SetNotificationsEnabled(bool value)
    {
        _suspendSideEffects = true;
        NotificationsEnabled = value;
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
    /// Reflects the real state of both toggles without raising their events — used at
    /// construction and whenever the settings panel reopens, so a change made elsewhere
    /// (e.g. the tray menu flipping start-on-boot) shows up correctly.
    /// </summary>
    public void SyncFromSystem(bool notificationsEnabled, bool startOnBoot)
    {
        _suspendSideEffects = true;
        NotificationsEnabled = notificationsEnabled;
        StartOnBoot = startOnBoot;
        _suspendSideEffects = false;
    }
}
