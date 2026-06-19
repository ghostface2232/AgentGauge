using Gauge.ViewModels;

namespace Gauge.Tests;

public sealed class GlobalSettingsViewModelTests
{
    [Fact]
    public void ConstructorSetsInitialStateWithoutRaisingEvents()
    {
        var notifications = 0;
        var startup = 0;
        var vm = new GlobalSettingsViewModel(notificationsEnabled: true, startOnBoot: true);
        vm.NotificationsToggleRequested += (_, _) => notifications++;
        vm.StartOnBootToggleRequested += (_, _) => startup++;

        Assert.True(vm.NotificationsEnabled);
        Assert.True(vm.StartOnBoot);
        Assert.Equal(0, notifications);
        Assert.Equal(0, startup);
    }

    [Fact]
    public void TogglingNotificationsRaisesRequestWithNewValue()
    {
        var vm = new GlobalSettingsViewModel(notificationsEnabled: true, startOnBoot: false);
        bool? requested = null;
        vm.NotificationsToggleRequested += (_, value) => requested = value;

        vm.NotificationsEnabled = false;

        Assert.False(requested);
    }

    [Fact]
    public void TogglingStartOnBootRaisesRequestWithNewValue()
    {
        var vm = new GlobalSettingsViewModel(notificationsEnabled: false, startOnBoot: false);
        bool? requested = null;
        vm.StartOnBootToggleRequested += (_, value) => requested = value;

        vm.StartOnBoot = true;

        Assert.True(requested);
    }

    [Fact]
    public void SetStartOnBootReflectsStateWithoutRaisingEvent()
    {
        var vm = new GlobalSettingsViewModel(notificationsEnabled: true, startOnBoot: true);
        var raised = 0;
        vm.StartOnBootToggleRequested += (_, _) => raised++;

        vm.SetStartOnBoot(false);

        Assert.False(vm.StartOnBoot);
        Assert.Equal(0, raised);
    }

    [Fact]
    public void SyncFromSystemReflectsBothTogglesWithoutRaisingEvents()
    {
        var vm = new GlobalSettingsViewModel(notificationsEnabled: true, startOnBoot: true);
        var notifications = 0;
        var startup = 0;
        vm.NotificationsToggleRequested += (_, _) => notifications++;
        vm.StartOnBootToggleRequested += (_, _) => startup++;

        vm.SyncFromSystem(notificationsEnabled: false, startOnBoot: false);

        Assert.False(vm.NotificationsEnabled);
        Assert.False(vm.StartOnBoot);
        Assert.Equal(0, notifications);
        Assert.Equal(0, startup);
    }
}
