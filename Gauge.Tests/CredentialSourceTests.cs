using Gauge.Models;
using Gauge.Services;

namespace Gauge.Tests;

public sealed class CredentialSourceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "GaugeTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task MissingFileIsCleanMissingState()
    {
        var source = Source();
        var result = await source.ReadAsync(ToolKind.ClaudeCode);
        Assert.Equal(CredentialReadStatus.Missing, result.Status);
    }

    [Fact]
    public async Task MalformedJsonIsInvalidWithoutLeakingContents()
    {
        Write(".claude/.credentials.json", "{ secret-token");
        var result = await Source().ReadAsync(ToolKind.ClaudeCode);
        Assert.Equal(CredentialReadStatus.Invalid, result.Status);
        Assert.DoesNotContain("secret-token", result.Message ?? "");
    }

    [Fact]
    public async Task ReadsCodexHomeAndClaudePlanMapping()
    {
        var codexHome = Path.Combine(_root, "custom-codex");
        WriteAt(Path.Combine(codexHome, "auth.json"), """{"tokens":{"access_token":"codex-secret","account_id":"acct"}}""");
        Write(".claude/.credentials.json", """{"claudeAiOauth":{"accessToken":"claude-secret","subscriptionType":"max","rateLimitTier":"default_claude_max_20x"}}""");
        var source = new CliCredentialSource(() => _root, () => codexHome);

        var codex = await source.ReadAsync(ToolKind.Codex);
        var claude = await source.ReadAsync(ToolKind.ClaudeCode);

        Assert.Equal("acct", codex.Credential?.AccountId);
        Assert.Equal("Max 20x", claude.Credential?.Plan);
        Assert.Equal(CredentialOwner.CliLocal, claude.Credential?.Owner);
    }

    private CliCredentialSource Source() => new(() => _root, () => null);
    private void Write(string relative, string text) => WriteAt(Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar)), text);
    private static void WriteAt(string path, string text) { Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, text); }
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
