using Gauge.Services;
using Xunit;

namespace Gauge.Tests;

public sealed class CliLocatorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gauge-cli-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private string Touch(params string[] parts)
    {
        var path = Path.Combine([_root, .. parts]);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "");
        return path;
    }

    [Fact]
    public void ClaudeDesktopBinaryPicksHighestVersionNotLexicalOrder()
    {
        Touch("2.1.99", "aaaa", "claude.exe");
        Touch("2.1.99", "aaaa", ".verified");
        var newest = Touch("2.1.309", "9078b9c1d466", "claude.exe");
        Touch("2.1.309", "9078b9c1d466", ".verified");
        Directory.CreateDirectory(Path.Combine(_root, "not-a-version"));

        Assert.Equal(newest, CliLocator.FindNewestClaudeBinary(_root));
    }

    [Fact]
    public void ClaudeDesktopBinarySkipsVersionWithoutExecutable()
    {
        var older = Touch("2.1.308", "c17a819afb40", "claude.exe");
        Directory.CreateDirectory(Path.Combine(_root, "2.1.309", "partial"));

        Assert.Equal(older, CliLocator.FindNewestClaudeBinary(_root));
    }

    [Fact]
    public void ClaudeDesktopBinaryStillDownloadingLosesToOlderVerifiedBuild()
    {
        var older = Touch("2.1.308", "c17a819afb40", "claude.exe");
        Touch("2.1.308", "c17a819afb40", ".verified");
        Touch("2.1.309", "9078b9c1d466", "claude.exe");

        Assert.Equal(older, CliLocator.FindNewestClaudeBinary(_root));
    }

    [Fact]
    public void ClaudeDesktopBinaryWithoutAnyMarkerFallsBackToNewest()
    {
        Touch("2.1.308", "c17a819afb40", "claude.exe");
        var newest = Touch("2.1.309", "9078b9c1d466", "claude.exe");

        Assert.Equal(newest, CliLocator.FindNewestClaudeBinary(_root));
    }

    [Fact]
    public void ClaudeDesktopBinaryMissingRootIsNull()
    {
        Assert.Null(CliLocator.FindNewestClaudeBinary(Path.Combine(_root, "absent")));
    }

    [Fact]
    public void CodexBinaryFoundInPackageLayout()
    {
        var binary = Touch("app", "resources", "codex.exe");

        Assert.Equal(binary, CliLocator.CodexBinaryIn(_root));
    }
}
