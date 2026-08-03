using Gauge.Localization;
using Gauge.Models;
using Gauge.ViewModels;

namespace Gauge.Tests;

public sealed class GlobalSettingsViewModelTests
{
    private static GlobalSettingsViewModel Create(
        bool notificationsEnabled = true,
        bool startOnBoot = false,
        UsageViewMode viewMode = UsageViewMode.Bar)
        => new(NotificationPreferences.Default with { Enabled = notificationsEnabled }, startOnBoot, viewMode);

    [Fact]
    public void ConstructorSetsInitialStateWithoutRaisingEvents()
    {
        var notifications = 0;
        var startup = 0;
        var viewModeChanges = 0;
        var kinds = 0;
        var vm = Create(notificationsEnabled: true, startOnBoot: true, viewMode: UsageViewMode.Gauge);
        vm.NotificationsToggleRequested += (_, _) => notifications++;
        vm.NotificationKindToggleRequested += (_, _) => kinds++;
        vm.StartOnBootToggleRequested += (_, _) => startup++;
        vm.ViewModeChangeRequested += (_, _) => viewModeChanges++;

        Assert.True(vm.NotificationsEnabled);
        Assert.True(vm.NotifyThresholds);
        Assert.True(vm.NotifyResets);
        Assert.True(vm.StartOnBoot);
        Assert.Equal((int)UsageViewMode.Gauge, vm.ViewModeIndex);
        Assert.Equal(0, notifications);
        Assert.Equal(0, kinds);
        Assert.Equal(0, startup);
        Assert.Equal(0, viewModeChanges);
    }

    [Fact]
    public void PickingViewModeRaisesRequestWithChosenMode()
    {
        var vm = Create();
        UsageViewMode? requested = null;
        vm.ViewModeChangeRequested += (_, mode) => requested = mode;

        vm.ViewModeIndex = (int)UsageViewMode.Gauge;

        Assert.Equal(UsageViewMode.Gauge, requested);
    }

    [Fact]
    public void TogglingNotificationsRaisesRequestWithNewValue()
    {
        var vm = Create();
        bool? requested = null;
        vm.NotificationsToggleRequested += (_, value) => requested = value;

        vm.NotificationsEnabled = false;

        Assert.False(requested);
    }

    [Fact]
    public void TogglingKindsRaisesRequestWithKindAndValue()
    {
        var vm = Create();
        var requests = new List<(UsageNotificationKind Kind, bool Enabled)>();
        vm.NotificationKindToggleRequested += (_, change) => requests.Add(change);

        vm.NotifyThresholds = false;
        vm.NotifyResets = false;

        Assert.Equal(
            [(UsageNotificationKind.Threshold, false), (UsageNotificationKind.Reset, false)],
            requests);
    }

    [Fact]
    public void PickingLanguageRaisesRequestWithChosenLanguage()
    {
        var vm = Create();
        AppLanguage? requested = null;
        vm.LanguageChangeRequested += (_, language) => requested = language;

        // Loc defaults to Korean in tests, so the constructor starts at index 0.
        Assert.Equal((int)AppLanguage.Korean, vm.LanguageIndex);

        vm.LanguageIndex = (int)AppLanguage.Japanese;

        Assert.Equal(AppLanguage.Japanese, requested);
    }

    [Fact]
    public void SetLanguageReflectsPersistedChoiceWithoutRaisingRequest()
    {
        var vm = Create();
        var requests = 0;
        vm.LanguageChangeRequested += (_, _) => requests++;
        vm.LanguageIndex = (int)AppLanguage.Japanese;

        vm.SetLanguage(AppLanguage.Korean);

        Assert.Equal((int)AppLanguage.Korean, vm.LanguageIndex);
        Assert.Equal(1, requests);
    }

    [Fact]
    public void TogglingStartOnBootRaisesRequestWithNewValue()
    {
        var vm = Create(notificationsEnabled: false);
        bool? requested = null;
        vm.StartOnBootToggleRequested += (_, value) => requested = value;

        vm.StartOnBoot = true;

        Assert.True(requested);
    }

    [Fact]
    public void SetStartOnBootReflectsStateWithoutRaisingEvent()
    {
        var vm = Create(startOnBoot: true);
        var raised = 0;
        vm.StartOnBootToggleRequested += (_, _) => raised++;

        vm.SetStartOnBoot(false);

        Assert.False(vm.StartOnBoot);
        Assert.Equal(0, raised);
    }

    [Fact]
    public void SyncFromSystemReflectsTogglesWithoutRaisingEvents()
    {
        var vm = Create(notificationsEnabled: true, startOnBoot: true);
        var notifications = 0;
        var kinds = 0;
        var startup = 0;
        vm.NotificationsToggleRequested += (_, _) => notifications++;
        vm.NotificationKindToggleRequested += (_, _) => kinds++;
        vm.StartOnBootToggleRequested += (_, _) => startup++;

        vm.SyncFromSystem(new NotificationPreferences(false, false, true), startOnBoot: false);

        Assert.False(vm.NotificationsEnabled);
        Assert.False(vm.NotifyThresholds);
        Assert.True(vm.NotifyResets);
        Assert.False(vm.StartOnBoot);
        Assert.Equal(0, notifications);
        Assert.Equal(0, kinds);
        Assert.Equal(0, startup);
    }
}
