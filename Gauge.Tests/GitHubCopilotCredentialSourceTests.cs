using Gauge.Models;
using Gauge.Providers;
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

    [Theory]
    [InlineData("github.com", true)]
    [InlineData("GitHub.com:Iv1.abc", true)]
    [InlineData("ghe.corp.example", false)]
    [InlineData("acme.ghe.com:Iv1.abc", false)]
    [InlineData("github.com.corp.example:Iv1.abc", false)]
    [InlineData("notgithub.com", false)]
    public async Task OnlyATokenIssuedForGitHubDotComIsUsed(string key, bool used)
    {
        using var temp = new TempDir();
        var copilotDir = Path.Combine(temp.Path, "github-copilot");
        Directory.CreateDirectory(copilotDir);
        await File.WriteAllTextAsync(Path.Combine(copilotDir, "hosts.json"),
            $$"""{ "{{key}}": { "oauth_token": "gho_file" } }""");
        var source = new GitHubCopilotCredentialSource(
            new FakeReader(null), localAppData: () => temp.Path, userProfile: () => temp.Path);

        var result = await source.ReadAsync(ToolKind.GitHubCopilot);

        Assert.Equal(used ? CredentialReadStatus.Available : CredentialReadStatus.Missing, result.Status);
    }

    [Fact]
    public async Task AGhThatCouldNotAnswerIsNotASignOut()
    {
        using var temp = new TempDir();
        var source = new GitHubCopilotCredentialSource(
            new ThrowingReader(), localAppData: () => temp.Path, userProfile: () => temp.Path);

        var result = await source.ReadAsync(ToolKind.GitHubCopilot);

        Assert.Equal(CredentialReadStatus.TemporarilyUnavailable, result.Status);
        Assert.Null(result.Credential);
    }

    [Fact]
    public async Task AGhThatCouldNotAnswerStillFallsBackToTheFile()
    {
        using var temp = new TempDir();
        var copilotDir = Path.Combine(temp.Path, "github-copilot");
        Directory.CreateDirectory(copilotDir);
        await File.WriteAllTextAsync(Path.Combine(copilotDir, "apps.json"),
            """{ "github.com:Iv1.abc": { "oauth_token": "gho_fromfile" } }""");
        var source = new GitHubCopilotCredentialSource(
            new ThrowingReader(), localAppData: () => temp.Path, userProfile: () => temp.Path);

        var result = await source.ReadAsync(ToolKind.GitHubCopilot);

        Assert.Equal("gho_fromfile", result.Credential!.AccessToken);
    }

    [Fact]
    public async Task ATemporarilyUnavailableSourceFailsTheReadSoTheLastValuesStay()
    {
        var provider = new GitHubCopilotProvider(new HttpClient(new NotCalledHandler()),
            new GitHubCopilotCredentialSource(new ThrowingReader(), () => EmptyDir, () => EmptyDir));

        await Assert.ThrowsAsync<HttpRequestException>(() => provider.GetSnapshotAsync(default));
    }

    private static string EmptyDir => Path.Combine(Path.GetTempPath(), "gauge-test-none-" + Guid.NewGuid().ToString("N"));

    private sealed class NotCalledHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new InvalidOperationException("No request may be made without a token.");
    }

    private sealed class ThrowingReader : IGitHubCliTokenReader
    {
        public Task<string?> ReadTokenAsync(CancellationToken cancellationToken)
            => throw new GitHubCliReadException("gh timed out.");
    }

    [Fact]
    public async Task IgnoresOtherTools()
    {
        var source = new GitHubCopilotCredentialSource(new FakeReader("gho_x"));
        var result = await source.ReadAsync(ToolKind.ClaudeCode);
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
