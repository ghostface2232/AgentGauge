using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Gauge.Localization;
using Gauge.Services;

namespace Gauge.ViewModels;

/// <summary>
/// Footer update control: shows the current version and a single action button
/// that checks GitHub Releases ("업데이트 확인") and, once a newer build is found,
/// switches to applying it ("업데이트"). Status text is surfaced as the button tooltip.
/// </summary>
public sealed partial class UpdateViewModel : ObservableObject
{
    private readonly UpdateService _service;
    private GitHubRelease? _available;

    public UpdateViewModel(UpdateService service)
    {
        _service = service;
        VersionText = service.CurrentVersion.ToString(3);
        ActionCommand = new AsyncRelayCommand(RunActionAsync, () => !IsBusy);
    }

    public string VersionText { get; }
    public IAsyncRelayCommand ActionCommand { get; }

    /// <summary>
    /// Raised after the installer has launched. The app should tear down and exit
    /// so the silent installer can replace its files and relaunch Gauge.
    /// </summary>
    public event EventHandler? ExitRequested;

    [ObservableProperty] public partial string StatusText { get; set; } = "";
    [ObservableProperty] public partial bool IsBusy { get; set; }
    [ObservableProperty] public partial bool IsUpdateAvailable { get; set; }

    public string ActionButtonText => IsUpdateAvailable ? Loc.Get("Update_Apply") : Loc.Get("Update_Check");

    partial void OnIsBusyChanged(bool value) => ActionCommand.NotifyCanExecuteChanged();

    partial void OnIsUpdateAvailableChanged(bool value) => OnPropertyChanged(nameof(ActionButtonText));

    /// <summary>Quiet check at launch; leaves the status blank unless an update is found.</summary>
    public async Task CheckInBackgroundAsync()
    {
        if (IsBusy) return;
        ApplyResult(await _service.CheckAsync(), quiet: true);
    }

    private Task RunActionAsync() => IsUpdateAvailable ? UpdateAsync() : CheckAsync();

    private async Task CheckAsync()
    {
        IsBusy = true;
        StatusText = Loc.Get("Update_Checking");
        try
        {
            ApplyResult(await _service.CheckAsync(), quiet: false);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void ApplyResult(UpdateCheckResult result, bool quiet)
    {
        switch (result.Status)
        {
            case UpdateStatus.UpdateAvailable:
                _available = result.Release;
                IsUpdateAvailable = true;
                StatusText = Loc.Get("Update_Available");
                break;
            case UpdateStatus.UpToDate:
                IsUpdateAvailable = false;
                StatusText = Loc.Get("Update_UpToDate");
                break;
            default:
                IsUpdateAvailable = false;
                if (!quiet) StatusText = Loc.Get("Update_CheckFailed");
                break;
        }
    }

    private async Task UpdateAsync()
    {
        if (_available is null) return;

        IsBusy = true;
        StatusText = Loc.Get("Update_Downloading");
        if (await _service.DownloadAndLaunchAsync(_available))
        {
            StatusText = Loc.Get("Update_Installing");
            ExitRequested?.Invoke(this, EventArgs.Empty);
        }
        else
        {
            StatusText = Loc.Get("Update_InstallFailed");
            IsBusy = false;
        }
    }
}
