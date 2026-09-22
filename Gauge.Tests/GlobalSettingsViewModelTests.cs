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
        UsageDisplayBasis displayBasis = UsageDisplayBasis.Used,
        bool showSparkline = true,
        WeeklyPaceModel paceModel = WeeklyPaceModel.Uniform)
        => new(notifications ?? NotificationPreferences.Default, startOnBoot, viewMode, displayBasis, showSparkline, paceModel);

    [Fact]
    public void ConstructorDefaultsPaceModelToUniform()
        => Assert.Equal((int)WeeklyPaceModel.Uniform, Create().PaceModelIndex);

    [Fact]
    public void ConstructorSetsPaceModelWithoutRaisingRequest()
    {
        var requests = 0;
        var vm = Create(paceModel: WeeklyPaceModel.Automatic);
        vm.PaceModelChangeRequested += (_, _) => requests++;
        Assert.Equal((int)WeeklyPaceModel.Automatic, vm.PaceModelIndex);
        Assert.Equal(0, requests);
    }

    [Fact]
    public void PickingPaceModelRaisesRequestWithChosenModel()
    {
        var vm = Create();
        var requested = new List<WeeklyPaceModel>();
        vm.PaceModelChangeRequested += (_, model) => requested.Add(model);

        vm.PaceModelIndex = (int)WeeklyPaceModel.WorkDays;
        vm.PaceModelIndex = (int)WeeklyPaceModel.Automatic;
        vm.PaceModelIndex = (int)WeeklyPaceModel.Uniform;

        Assert.Equal([WeeklyPaceModel.WorkDays, WeeklyPaceModel.Automatic, WeeklyPaceModel.Uniform], requested);
    }

    [Fact]
    public void ReflectingPaceModelDoesNotRaiseRequest()
    {
        // The reflect-back after a refused settings.json write must not loop as a new request.
        var vm = Create();
        var requests = 0;
        vm.PaceModelChangeRequested += (_, _) => requests++;
        vm.SetPaceModel(WeeklyPaceModel.WorkDays);
        Assert.Equal((int)WeeklyPaceModel.WorkDays, vm.PaceModelIndex);
        Assert.Equal(0, requests);
    }

    [Fact]
    public void PaceModelOptionsFollowEnumOrder()
    {
        // The ComboBox index is cast straight to the enum, so the labels must line up.
        var vm = Create();
        Assert.Equal(3, vm.PaceModelOptions.Count);
        Assert.Equal("매일 균등 (7일)", vm.PaceModelOptions[(int)WeeklyPaceModel.Uniform]);
        Assert.Equal("근무일 (월–금)", vm.PaceModelOptions[(int)WeeklyPaceModel.WorkDays]);
        Assert.Equal("자동 (사용 추이)", vm.PaceModelOptions[(int)WeeklyPaceModel.Automatic]);
    }

    [Fact]
    public void ConstructorSetsInitialStateWithoutRaisingEvents()
    {
        var startup = 0;
        var viewModeChanges = 0;
        var basisChanges = 0;
        var sparklineChanges = 0;
        var kinds = 0;
        var vm = Create(startOnBoot: true, viewMode: UsageViewMode.Gauge, displayBasis: UsageDisplayBasis.Remaining,
            showSparkline: false);
        vm.NotificationKindToggleRequested += (_, _) => kinds++;
        vm.StartOnBootToggleRequested += (_, _) => startup++;
        vm.ViewModeChangeRequested += (_, _) => viewModeChanges++;
        vm.DisplayBasisChangeRequested += (_, _) => basisChanges++;
        vm.SparklineToggleRequested += (_, _) => sparklineChanges++;

        Assert.True(vm.NotifyThresholds);
        Assert.True(vm.NotifyResets);
        Assert.True(vm.StartOnBoot);
        Assert.Equal((int)UsageViewMode.Gauge, vm.ViewModeIndex);
        Assert.Equal((int)UsageDisplayBasis.Remaining, vm.DisplayBasisIndex);
        Assert.False(vm.ShowSparkline);
        Assert.Equal(0, kinds);
        Assert.Equal(0, startup);
        Assert.Equal(0, viewModeChanges);
        Assert.Equal(0, basisChanges);
        Assert.Equal(0, sparklineChanges);
    }

    [Fact]
    public void ConstructorDefaultsSparklineToShown()
        => Assert.True(Create().ShowSparkline);

    [Fact]
    public void TogglingSparklineRaisesRequestWithValue()
    {
        var vm = Create();
        var requested = new List<bool>();
        vm.SparklineToggleRequested += (_, show) => requested.Add(show);

        vm.ShowSparkline = false;
        vm.ShowSparkline = true;

        Assert.Equal([false, true], requested);
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
    public void SetViewModeReflectsThePersistedModeWithoutRaisingRequest()
    {
        // A refused settings.json write must snap the dropdown back to what is on disk,
        // and that reflect-back must not bounce out as a fresh change request.
        var vm = Create();
        var requests = 0;
        vm.ViewModeChangeRequested += (_, _) => requests++;
        vm.ViewModeIndex = (int)UsageViewMode.Gauge;

        vm.SetViewMode(UsageViewMode.Bar);

        Assert.Equal((int)UsageViewMode.Bar, vm.ViewModeIndex);
        Assert.Equal(1, requests);
    }

    [Fact]
    public void SetDisplayBasisReflectsThePersistedBasisWithoutRaisingRequest()
    {
        var vm = Create();
        var requests = 0;
        vm.DisplayBasisChangeRequested += (_, _) => requests++;
        vm.DisplayBasisIndex = (int)UsageDisplayBasis.Remaining;

        vm.SetDisplayBasis(UsageDisplayBasis.Used);

        Assert.Equal((int)UsageDisplayBasis.Used, vm.DisplayBasisIndex);
        Assert.Equal(1, requests);
    }

    [Fact]
    public void SetShowSparklineReflectsThePersistedStateWithoutRaisingRequest()
    {
        var vm = Create();
        var requests = 0;
        vm.SparklineToggleRequested += (_, _) => requests++;
        vm.ShowSparkline = false;

        vm.SetShowSparkline(true);

        Assert.True(vm.ShowSparkline);
        Assert.Equal(1, requests);
    }

    [Fact]
    public void SettingsNoticeStartsEmptyDrivesItsRowAndRaisesNoRequest()
    {
        // It is App's report on what happened to settings.json, not a setting the user
        // picks, so setting it must not look like a change request to anything listening.
        var vm = Create();
        var requests = 0;
        vm.SparklineToggleRequested += (_, _) => requests++;
        vm.DisplayBasisChangeRequested += (_, _) => requests++;
        vm.ViewModeChangeRequested += (_, _) => requests++;
        vm.NotificationKindToggleRequested += (_, _) => requests++;
        Assert.Null(vm.SettingsNotice);
        Assert.False(vm.HasSettingsNotice);

        vm.SettingsNotice = "설정을 저장하지 못해 변경을 되돌렸습니다.";
        Assert.True(vm.HasSettingsNotice);

        // Clearing must collapse the row again, which is what a later successful write does.
        vm.SettingsNotice = null;
        Assert.False(vm.HasSettingsNotice);
        Assert.Equal(0, requests);
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
