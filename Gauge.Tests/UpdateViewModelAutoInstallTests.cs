using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Gauge.Services;
using Gauge.ViewModels;

namespace Gauge.Tests;

/// <summary>
/// The launch-time install runs unattended and the app exits as soon as Setup starts, so a Setup
/// that keeps failing must not be able to end the app on every logon.
/// </summary>
public sealed class UpdateViewModelAutoInstallTests : IDisposable
{
    private const string InstallerBytes = "installer-bytes";

    private readonly string _downloads = Path.Combine(Path.GetTempPath(), "GaugeUpdateTests", Guid.NewGuid().ToString("N"));
    private readonly FakeLauncher _launcher = new();

    public void Dispose()
    {
        if (Directory.Exists(_downloads)) Directory.Delete(_downloads, recursive: true);
    }

    [Fact]
    public async Task AFirstAttemptIsRecordedAndLaunched()
    {
        var attempts = new FakeAttempts();
        var (viewModel, exits) = Create(attempts);

        await viewModel.CheckInBackgroundAsync(installIfAvailable: true);

        Assert.Equal(new Version(9, 9, 9), attempts.Recorded);
        Assert.NotNull(_launcher.LaunchedPath);
        Assert.Equal(1, exits());
    }

    [Fact]
    public async Task AVersionAlreadyTriedStaysOnTheButtonInsteadOfRunningAgain()
    {
        var attempts = new FakeAttempts { Attempted = new Version(9, 9, 9) };
        var (viewModel, exits) = Create(attempts);

        await viewModel.CheckInBackgroundAsync(installIfAvailable: true);

        Assert.Null(_launcher.LaunchedPath);
        Assert.Equal(0, exits());
        Assert.True(viewModel.IsUpdateAvailable);
        Assert.False(viewModel.IsBusy);
    }

    [Fact]
    public async Task TheButtonStillInstallsAVersionAlreadyTried()
    {
        var attempts = new FakeAttempts { Attempted = new Version(9, 9, 9) };
        var (viewModel, _) = Create(attempts);
        await viewModel.CheckInBackgroundAsync(installIfAvailable: true);

        await viewModel.ActionCommand.ExecuteAsync(null);

        Assert.NotNull(_launcher.LaunchedPath);
    }

    [Fact]
    public async Task ANewerReleaseIsTriedEvenAfterAnEarlierOneFailed()
    {
        var attempts = new FakeAttempts { Attempted = new Version(9, 9, 8) };
        var (viewModel, _) = Create(attempts);

        await viewModel.CheckInBackgroundAsync(installIfAvailable: true);

        Assert.NotNull(_launcher.LaunchedPath);
    }

    [Fact]
    public async Task AnAttemptThatCouldNotBeRecordedIsNotLaunched()
    {
        // Unrecorded, a failing Setup would go unnoticed by the next launch too.
        var attempts = new FakeAttempts { CanRecord = false };
        var (viewModel, exits) = Create(attempts);

        await viewModel.CheckInBackgroundAsync(installIfAvailable: true);

        Assert.Null(_launcher.LaunchedPath);
        Assert.Equal(0, exits());
    }

    [Fact]
    public async Task AFailedDownloadIsNotCountedAsAnAttempt()
    {
        var attempts = new FakeAttempts();
        var (viewModel, _) = Create(attempts, installer: "corrupted");

        await viewModel.CheckInBackgroundAsync(installIfAvailable: true);

        Assert.Null(attempts.Recorded);
        Assert.Null(_launcher.LaunchedPath);
    }

    [Fact]
    public async Task AQuietCheckAloneInstallsNothing()
    {
        var attempts = new FakeAttempts();
        var (viewModel, _) = Create(attempts);

        await viewModel.CheckInBackgroundAsync();

        Assert.True(viewModel.IsUpdateAvailable);
        Assert.Null(attempts.Recorded);
        Assert.Null(_launcher.LaunchedPath);
    }

    private (UpdateViewModel ViewModel, Func<int> Exits) Create(IAutoInstallAttempts attempts, string installer = InstallerBytes)
    {
        var service = new UpdateService(
            new HttpClient(new ReleaseHandler(installer)),
            new Version(0, 2, 4),
            _launcher,
            Architecture.X64,
            _downloads);
        var viewModel = new UpdateViewModel(service, attempts);
        var exits = 0;
        viewModel.ExitRequested += (_, _) => exits++;
        return (viewModel, () => exits);
    }

    private static string Sha256Of(string body)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(body)));

    private sealed class FakeAttempts : IAutoInstallAttempts
    {
        public Version? Attempted { get; init; }
        public Version? Recorded { get; private set; }
        public bool CanRecord { get; init; } = true;

        public bool WasAttempted(Version version) => version == Attempted;

        public bool TryRecordAttempt(Version version)
        {
            if (CanRecord) Recorded = version;
            return CanRecord;
        }
    }

    private sealed class FakeLauncher : IInstallerLauncher
    {
        public string? LaunchedPath { get; private set; }

        public bool Launch(string installerPath, string arguments)
        {
            LaunchedPath = installerPath;
            return true;
        }
    }

    // The release always promises InstallerBytes; what is served for the asset can differ.
    private sealed class ReleaseHandler(string installer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.RequestUri!.Host == "api.github.com"
                ? $$"""
                  {
                    "tag_name": "v9.9.9",
                    "assets": [ { "name": "GaugeSetup-win-x64.exe", "browser_download_url": "https://example.test/setup.exe", "digest": "sha256:{{Sha256Of(InstallerBytes)}}" } ]
                  }
                  """
                : installer;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8),
            });
        }
    }
}
