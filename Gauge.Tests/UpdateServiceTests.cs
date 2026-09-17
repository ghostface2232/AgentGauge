using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Gauge.Services;

namespace Gauge.Tests;

/// <summary>
/// Update pipeline with injected HttpClient/version/launcher: release-tag parsing,
/// version comparison against the running build, the asset digest GitHub publishes,
/// and installer download/verification/launch failure. Closes the "Update version
/// comparison" and "Installer execution failure" rows from COVERAGE.md.
/// </summary>
public sealed class UpdateServiceTests
{
    // Well-formed but arbitrary: CheckAsync only needs the digest's shape.
    private static readonly string Digest = "sha256:" + new string('a', 64);

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
          "assets": [ { "name": "GaugeSetup-win-x64.exe", "browser_download_url": "https://example.test/setup.exe", "digest": "{{Digest}}" } ]
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
        var service = Service($$"""{ "tag_name": "v9.9.9", "assets": [ { "name": "other.zip", "browser_download_url": "https://example.test/o.zip", "digest": "{{Digest}}" } ] }""");
        Assert.Equal(UpdateStatus.CheckFailed, (await service.CheckAsync()).Status);
    }

    [Theory]
    [InlineData("sha256:0123456789ABCDEF0123456789abcdef0123456789ABCDEF0123456789abcdef", true, "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")]
    [InlineData("SHA256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef", false, "")]
    [InlineData("md5:0123456789abcdef0123456789abcdef", false, "")]
    [InlineData("sha256:0123456789abcdef", false, "")]
    [InlineData("sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdeg", false, "")]
    [InlineData("", false, "")]
    [InlineData(null, false, "")]
    public void ParsesGitHubAssetDigests(string? digest, bool expectedOk, string expectedSha256)
    {
        var ok = UpdateService.TryParseSha256Digest(digest, out var sha256);
        Assert.Equal(expectedOk, ok);
        Assert.Equal(expectedSha256, sha256);
    }

    [Fact]
    public async Task CheckCarriesTheAssetDigestOnTheRelease()
    {
        var upper = "sha256:" + new string('B', 64);
        var result = await Service(ReleaseJson("v9.9.9", upper)).CheckAsync();
        Assert.Equal(UpdateStatus.UpdateAvailable, result.Status);
        Assert.Equal(new string('b', 64), result.Release!.Sha256);
    }

    [Theory]
    [InlineData("""{ "name": "GaugeSetup-win-x64.exe", "browser_download_url": "https://example.test/setup.exe" }""")]
    [InlineData("""{ "name": "GaugeSetup-win-x64.exe", "browser_download_url": "https://example.test/setup.exe", "digest": null }""")]
    [InlineData("""{ "name": "GaugeSetup-win-x64.exe", "browser_download_url": "https://example.test/setup.exe", "digest": "sha512:00" }""")]
    public async Task AssetWithoutUsableDigestIsNotOffered(string asset)
    {
        // A download that cannot be verified is never offered, exactly like a missing asset.
        var service = Service($$"""{ "tag_name": "v9.9.9", "assets": [ {{asset}} ] }""");
        var result = await service.CheckAsync();
        Assert.Equal(UpdateStatus.CheckFailed, result.Status);
        Assert.Null(result.Release);
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
        var service = Service(ReleaseJson("v9.9.9", Digest), architecture: Architecture.Arm64);
        Assert.Equal(UpdateStatus.CheckFailed, (await service.CheckAsync()).Status);
    }

    [Fact]
    public async Task Arm64BuildPicksTheArm64AssetWhenPresent()
    {
        var both = $$"""
        {
          "tag_name": "v9.9.9",
          "assets": [
            { "name": "GaugeSetup-win-x64.exe", "browser_download_url": "https://example.test/x64.exe", "digest": "{{Digest}}" },
            { "name": "GaugeSetup-win-arm64.exe", "browser_download_url": "https://example.test/arm64.exe", "digest": "{{Digest}}" }
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
        var release = Release("v9.9.9", Sha256Of("installer-bytes"));

        var succeeding = new FakeLauncher(result: true);
        Assert.True(await Service("installer-bytes", launcher: succeeding).DownloadAndLaunchAsync(release));
        Assert.NotNull(succeeding.LaunchedPath);
        Assert.True(File.Exists(succeeding.LaunchedPath));
        Assert.Contains("/VERYSILENT", succeeding.Arguments);

        var failing = new FakeLauncher(result: false);
        Assert.False(await Service("installer-bytes", launcher: failing).DownloadAndLaunchAsync(release));
    }

    [Fact]
    public async Task DigestMismatchNeverLaunchesAndDiscardsTheDownload()
    {
        // The bytes served differ from what the release promised: nothing may run, and the
        // rejected file must not linger where a later attempt could pick it up.
        var launcher = new FakeLauncher(result: true);
        var release = Release("v9.9.8", Sha256Of("what-the-release-promised"));

        Assert.False(await Service("what-actually-arrived", launcher: launcher).DownloadAndLaunchAsync(release));
        Assert.Null(launcher.LaunchedPath);
        Assert.False(File.Exists(Path.Combine(Path.GetTempPath(), "Gauge", "GaugeSetup-v9.9.8.exe")));
    }

    [Fact]
    public async Task DownloadFailureReturnsFalseWithoutLaunching()
    {
        var launcher = new FakeLauncher(result: true);
        var service = Service("nope", HttpStatusCode.NotFound, launcher);

        Assert.False(await service.DownloadAndLaunchAsync(Release("v9.9.9", Sha256Of("nope"))));
        Assert.Null(launcher.LaunchedPath);
    }

    private static string ReleaseJson(string tag, string digest) => $$"""
        {
          "tag_name": "{{tag}}",
          "assets": [ { "name": "GaugeSetup-win-x64.exe", "browser_download_url": "https://example.test/setup.exe", "digest": "{{digest}}" } ]
        }
        """;

    private static GitHubRelease Release(string tag, string sha256)
        => new(new Version(9, 9, 9), tag, "https://example.test/setup.exe", sha256);

    private static string Sha256Of(string body)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(body)));

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
