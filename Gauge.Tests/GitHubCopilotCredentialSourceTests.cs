using Gauge.Models;
using Gauge.Services;

namespace Gauge.Tests;

public sealed class GitHubCopilotCredentialSourceTests
{
    [Fact]
    public async Task UsesGhCliTokenWhenAvailable()
    {
        var source = new GitHubCopilotCredentialSource(new FakeReader("gho_fromgh"));

        var result = await source.ReadAsync(ToolKind.GitHubCopilot);

        Assert.Equal(CredentialReadStatus.Available, result.Status);
        Assert.Equal("gho_fromgh", result.Credential!.AccessToken);
    }

    [Fact]
    public async Task FallsBackToAppsJsonFileWhenGhAbsent()
    {
        using var temp = new TempDir();
        var copilotDir = Path.Combine(temp.Path, "github-copilot");
        Directory.CreateDirectory(copilotDir);
        await File.WriteAllTextAsync(Path.Combine(copilotDir, "apps.json"),
            """{ "github.com:Iv1.abc": { "user": "octocat", "oauth_token": "gho_fromfile" } }""");

        var source = new GitHubCopilotCredentialSource(
            new FakeReader(null), localAppData: () => temp.Path, userProfile: () => temp.Path);

        var result = await source.ReadAsync(ToolKind.GitHubCopilot);

        Assert.Equal(CredentialReadStatus.Available, result.Status);
        Assert.Equal("gho_fromfile", result.Credential!.AccessToken);
    }

    [Fact]
    public async Task MissingWhenNeitherSourceHasToken()
    {
        using var temp = new TempDir(); // empty: no github-copilot files
        var source = new GitHubCopilotCredentialSource(
            new FakeReader(null), localAppData: () => temp.Path, userProfile: () => temp.Path);

        var result = await source.ReadAsync(ToolKind.GitHubCopilot);

        Assert.Equal(CredentialReadStatus.Missing, result.Status);
    }

    [Fact]
    public async Task IgnoresOtherTools()
    {
        var source = new GitHubCopilotCredentialSource(new FakeReader("gho_x"));
        var result = await source.ReadAsync(ToolKind.Cursor);
        Assert.Equal(CredentialReadStatus.Missing, result.Status);
    }

    private sealed class FakeReader(string? token) : IGitHubCliTokenReader
    {
        public Task<string?> ReadTokenAsync(CancellationToken cancellationToken) => Task.FromResult(token);
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "gauge-test-" + Guid.NewGuid().ToString("N"));
        public TempDir() => Directory.CreateDirectory(Path);
        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }
}
