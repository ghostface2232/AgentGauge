using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using Gauge.Services;

namespace Gauge.Tests;

/// <summary>
/// Update pipeline with injected HttpClient/version/launcher: release-tag parsing,
/// version comparison against the running build, and installer download/launch failure.
/// Closes the "Update version comparison" and "Installer execution failure" rows from
/// COVERAGE.md.
/// </summary>
public sealed class UpdateServiceTests
{
    [Theory]
    [InlineData("v0.2.4", true, "0.2.4")]
    [InlineData("V1.2.3", true, "1.2.3")]
    [InlineData("0.3.0-beta.1", true, "0.3.0")]
    [InlineData("v2.0", true, "2.0.0")]
    [InlineData("not-a-version", false, "0.0.0")]
    [InlineData("", false, "0.0.0")]
    public void ParsesReleaseTags(string tag, bool expectedOk, string expectedVersion)
    {
        var ok = UpdateService.TryParseVersion(tag, out var version);
        Assert.Equal(expectedOk, ok);
        Assert.Equal(Version.Parse(expectedVersion), version);
    }

    [Theory]
    [InlineData("v9.9.9", UpdateStatus.UpdateAvailable)]
    [InlineData("v0.2.4", UpdateStatus.UpToDate)]
    [InlineData("v0.1.0", UpdateStatus.UpToDate)]
    public async Task ComparesLatestReleaseAgainstCurrentVersion(string tag, UpdateStatus expected)
    {
        var json = $$"""
        {
          "tag_name": "{{tag}}",
          "assets": [ { "name": "GaugeSetup-win-x64.exe", "browser_download_url": "https://example.test/setup.exe" } ]
        }
        """;
        var service = Service(json);

        var result = await service.CheckAsync();

        Assert.Equal(expected, result.Status);
        Assert.Equal(new Version(0, 2, 4), result.CurrentVersion);
    }

    [Fact]
    public async Task MissingInstallerAssetIsCheckFailed()
    {
        var service = Service("""{ "tag_name": "v9.9.9", "assets": [ { "name": "other.zip", "browser_download_url": "https://example.test/o.zip" } ] }""");
        Assert.Equal(UpdateStatus.CheckFailed, (await service.CheckAsync()).Status);
    }

    [Theory]
    [InlineData(Architecture.X64, "GaugeSetup-win-x64.exe")]
    [InlineData(Architecture.Arm64, "GaugeSetup-win-arm64.exe")]
    [InlineData(Architecture.X86, "GaugeSetup-win-x64.exe")]
    public void SelectsInstallerAssetByProcessArchitecture(Architecture architecture, string expected)
        => Assert.Equal(expected, UpdateService.AssetNameFor(architecture));

    [Fact]
    public async Task Arm64BuildNeverAcceptsTheX64Asset()
    {
        // A release that ships only the x64 installer must read as CheckFailed on an
        // ARM64 build — silently installing the wrong-architecture payload is worse
        // than reporting no update.
        var x64Only = """
        {
          "tag_name": "v9.9.9",
          "assets": [ { "name": "GaugeSetup-win-x64.exe", "browser_download_url": "https://example.test/setup.exe" } ]
        }
        """;
        var service = Service(x64Only, architecture: Architecture.Arm64);
        Assert.Equal(UpdateStatus.CheckFailed, (await service.CheckAsync()).Status);
    }

    [Fact]
    public async Task Arm64BuildPicksTheArm64AssetWhenPresent()
    {
        var both = """
        {
          "tag_name": "v9.9.9",
          "assets": [
            { "name": "GaugeSetup-win-x64.exe", "browser_download_url": "https://example.test/x64.exe" },
            { "name": "GaugeSetup-win-arm64.exe", "browser_download_url": "https://example.test/arm64.exe" }
          ]
        }
        """;
        var result = await Service(both, architecture: Architecture.Arm64).CheckAsync();
        Assert.Equal(UpdateStatus.UpdateAvailable, result.Status);
        Assert.Equal("https://example.test/arm64.exe", result.Release!.DownloadUrl);
    }

    [Fact]
    public async Task NetworkFailureIsCheckFailedNotThrow()
    {
        var service = Service("{}", HttpStatusCode.InternalServerError);
        Assert.Equal(UpdateStatus.CheckFailed, (await service.CheckAsync()).Status);
    }

    [Fact]
    public async Task DownloadAndLaunchReportsLauncherOutcome()
    {
        var release = new GitHubRelease(new Version(9, 9, 9), "v9.9.9", "https://example.test/setup.exe");

        var succeeding = new FakeLauncher(result: true);
        Assert.True(await Service("installer-bytes", launcher: succeeding).DownloadAndLaunchAsync(release));
        Assert.NotNull(succeeding.LaunchedPath);
        Assert.True(File.Exists(succeeding.LaunchedPath));
        Assert.Contains("/VERYSILENT", succeeding.Arguments);

        var failing = new FakeLauncher(result: false);
        Assert.False(await Service("installer-bytes", launcher: failing).DownloadAndLaunchAsync(release));
    }

    [Fact]
    public async Task DownloadFailureReturnsFalseWithoutLaunching()
    {
        var launcher = new FakeLauncher(result: true);
        var service = Service("nope", HttpStatusCode.NotFound, launcher);
        var release = new GitHubRelease(new Version(9, 9, 9), "v9.9.9", "https://example.test/setup.exe");

        Assert.False(await service.DownloadAndLaunchAsync(release));
        Assert.Null(launcher.LaunchedPath);
    }

    private static UpdateService Service(
        string body,
        HttpStatusCode status = HttpStatusCode.OK,
        IInstallerLauncher? launcher = null,
        Architecture architecture = Architecture.X64)
        => new(
            new HttpClient(new StubHandler(body, status)),
            new Version(0, 2, 4),
            launcher ?? new FakeLauncher(result: true),
            architecture);

    private sealed class FakeLauncher(bool result) : IInstallerLauncher
    {
        public string? LaunchedPath { get; private set; }
        public string Arguments { get; private set; } = "";

        public bool Launch(string installerPath, string arguments)
        {
            LaunchedPath = installerPath;
            Arguments = arguments;
            return result;
        }
    }

    private sealed class StubHandler(string body, HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
    }
}
