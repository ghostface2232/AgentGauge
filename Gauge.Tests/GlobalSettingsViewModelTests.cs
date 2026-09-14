using Gauge.Localization;
using Gauge.Models;
using Gauge.ViewModels;

namespace Gauge.Tests;

public sealed class GlobalSettingsViewModelTests
{
    private static GlobalSettingsViewModel Create(
        NotificationPreferences? notifications = null,
        bool startOnBoot = false,
        UsageViewMode viewMode = UsageViewMode.Bar,
        UsageDisplayBasis displayBasis = UsageDisplayBasis.Used)
        => new(notifications ?? NotificationPreferences.Default, startOnBoot, viewMode, displayBasis);

    [Fact]
    public void ConstructorSetsInitialStateWithoutRaisingEvents()
    {
        var startup = 0;
        var viewModeChanges = 0;
        var basisChanges = 0;
        var kinds = 0;
        var vm = Create(startOnBoot: true, viewMode: UsageViewMode.Gauge, displayBasis: UsageDisplayBasis.Remaining);
        vm.NotificationKindToggleRequested += (_, _) => kinds++;
        vm.StartOnBootToggleRequested += (_, _) => startup++;
        vm.ViewModeChangeRequested += (_, _) => viewModeChanges++;
        vm.DisplayBasisChangeRequested += (_, _) => basisChanges++;

        Assert.True(vm.NotifyThresholds);
        Assert.True(vm.NotifyResets);
        Assert.True(vm.StartOnBoot);
        Assert.Equal((int)UsageViewMode.Gauge, vm.ViewModeIndex);
        Assert.Equal((int)UsageDisplayBasis.Remaining, vm.DisplayBasisIndex);
        Assert.Equal(0, kinds);
        Assert.Equal(0, startup);
        Assert.Equal(0, viewModeChanges);
        Assert.Equal(0, basisChanges);
    }

    [Fact]
    public void ConstructorDefaultsDisplayBasisToUsed()
        => Assert.Equal((int)UsageDisplayBasis.Used, Create().DisplayBasisIndex);

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
    public void PickingDisplayBasisRaisesRequestWithChosenBasis()
    {
        var vm = Create();
        var requested = new List<UsageDisplayBasis>();
        vm.DisplayBasisChangeRequested += (_, basis) => requested.Add(basis);

        vm.DisplayBasisIndex = (int)UsageDisplayBasis.Remaining;
        vm.DisplayBasisIndex = (int)UsageDisplayBasis.Used;

        Assert.Equal([UsageDisplayBasis.Remaining, UsageDisplayBasis.Used], requested);
    }

    [Fact]
    public void DisplayBasisOptionsFollowEnumOrder()
    {
        // The ComboBox index is cast straight to the enum, so the labels must line up.
        var vm = Create();
        Assert.Equal(2, vm.DisplayBasisOptions.Count);
        Assert.Equal("사용량", vm.DisplayBasisOptions[(int)UsageDisplayBasis.Used]);
        Assert.Equal("남은 사용량", vm.DisplayBasisOptions[(int)UsageDisplayBasis.Remaining]);
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
        var vm = Create(new NotificationPreferences(false, false));
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
        var vm = Create(startOnBoot: true);
        var kinds = 0;
        var startup = 0;
        vm.NotificationKindToggleRequested += (_, _) => kinds++;
        vm.StartOnBootToggleRequested += (_, _) => startup++;

        vm.SyncFromSystem(new NotificationPreferences(false, true), startOnBoot: false);

        Assert.False(vm.NotifyThresholds);
        Assert.True(vm.NotifyResets);
        Assert.False(vm.StartOnBoot);
        Assert.Equal(0, kinds);
        Assert.Equal(0, startup);
    }

    [Fact]
    public void SyncNotificationsReflectsATrayToggleWithoutRaisingRequests()
    {
        // The tray menu shows the same two switches, so a toggle made there while the panel
        // is open must land here — and must not bounce back as a fresh request.
        var vm = Create();
        var kinds = 0;
        vm.NotificationKindToggleRequested += (_, _) => kinds++;

        vm.SyncNotifications(new NotificationPreferences(false, true));

        Assert.False(vm.NotifyThresholds);
        Assert.True(vm.NotifyResets);
        Assert.Equal(0, kinds);
    }
}
