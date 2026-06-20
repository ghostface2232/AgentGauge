using System.Text;
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

    [Fact]
    public async Task ExpiredClaudeTokenIsInvalidWithReloginMessage()
    {
        var pastMs = DateTimeOffset.UtcNow.AddDays(-1).ToUnixTimeMilliseconds();
        Write(".claude/.credentials.json", $$"""{ "claudeAiOauth": { "accessToken": "t", "expiresAt": {{pastMs}} } }""");

        var result = await Source().ReadAsync(ToolKind.ClaudeCode);

        Assert.Equal(CredentialReadStatus.Invalid, result.Status);
        Assert.Contains("만료", result.Message ?? "");
    }

    [Fact]
    public async Task UnexpiredClaudeTokenIsAvailable()
    {
        var futureMs = DateTimeOffset.UtcNow.AddDays(30).ToUnixTimeMilliseconds();
        Write(".claude/.credentials.json", $$"""{ "claudeAiOauth": { "accessToken": "t", "expiresAt": {{futureMs}} } }""");

        var result = await Source().ReadAsync(ToolKind.ClaudeCode);

        Assert.Equal(CredentialReadStatus.Available, result.Status);
        Assert.NotNull(result.Credential?.ExpiresAt);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"claudeAiOauth":{}}""")]
    [InlineData("""{"claudeAiOauth":{"accessToken":""}}""")]
    public async Task ClaudeWithoutTokenIsInvalid(string json)
    {
        Write(".claude/.credentials.json", json);
        var result = await Source().ReadAsync(ToolKind.ClaudeCode);
        Assert.Equal(CredentialReadStatus.Invalid, result.Status);
    }

    [Theory]
    [InlineData("max", "default_claude_max_5x", "Max 5x")]
    [InlineData("max", null, "Max")]
    [InlineData("pro", null, "Pro")]
    public async Task MapsClaudePlanThroughRead(string subscription, string? tier, string expected)
    {
        var tierField = tier is null ? "" : $", \"rateLimitTier\": \"{tier}\"";
        Write(".claude/.credentials.json",
            $$"""{ "claudeAiOauth": { "accessToken": "t", "subscriptionType": "{{subscription}}"{{tierField}} } }""");

        var result = await Source().ReadAsync(ToolKind.ClaudeCode);

        Assert.Equal(expected, result.Credential?.Plan);
    }

    [Fact]
    public async Task CodexWithoutAccessTokenIsInvalid()
    {
        Write(".codex/auth.json", """{"tokens":{}}""");
        var result = await Source().ReadAsync(ToolKind.Codex);
        Assert.Equal(CredentialReadStatus.Invalid, result.Status);
    }

    [Fact]
    public async Task CodexFallsBackToUserProfileWhenHomeUnset()
    {
        // codexHome returns null, so the source must read <userProfile>/.codex/auth.json.
        Write(".codex/auth.json", """{"tokens":{"access_token":"codex-secret","account_id":"acct"}}""");

        var result = await Source().ReadAsync(ToolKind.Codex);

        Assert.Equal(CredentialReadStatus.Available, result.Status);
        Assert.Equal("acct", result.Credential?.AccountId);
    }

    [Fact]
    public async Task ExpiredCodexJwtIsInvalidWithReloginMessage()
    {
        var token = FakeJwt(DateTimeOffset.UtcNow.AddDays(-1).ToUnixTimeSeconds());
        Write(".codex/auth.json", CodexAuthJson(token));

        var result = await Source().ReadAsync(ToolKind.Codex);

        Assert.Equal(CredentialReadStatus.Invalid, result.Status);
        Assert.Contains("만료", result.Message ?? "");
    }

    [Fact]
    public async Task UnexpiredCodexJwtIsAvailableWithExpiry()
    {
        var exp = DateTimeOffset.UtcNow.AddDays(9).ToUnixTimeSeconds();
        Write(".codex/auth.json", CodexAuthJson(FakeJwt(exp)));

        var result = await Source().ReadAsync(ToolKind.Codex);

        Assert.Equal(CredentialReadStatus.Available, result.Status);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(exp), result.Credential?.ExpiresAt);
    }

    [Fact]
    public async Task CodexNonJwtTokenHasNoExpiryAndStaysAvailable()
    {
        // An opaque (non-JWT) token can't be decoded for an exp, so it must not be treated
        // as expired — it stays Available with no expiry, preserving prior behavior.
        Write(".codex/auth.json", CodexAuthJson("opaque-token"));

        var result = await Source().ReadAsync(ToolKind.Codex);

        Assert.Equal(CredentialReadStatus.Available, result.Status);
        Assert.Null(result.Credential?.ExpiresAt);
    }

    [Fact]
    public async Task CodexJwtWithOutOfRangeExpIsHandledNotThrown()
    {
        // A corrupt token with an absurd exp must not escape as ArgumentOutOfRangeException;
        // it degrades to "no expiry known" → Available (the server will reject it if bad).
        var token = FakeJwtRawExp("999999999999999");
        Write(".codex/auth.json", CodexAuthJson(token));

        var result = await Source().ReadAsync(ToolKind.Codex);

        Assert.Equal(CredentialReadStatus.Available, result.Status);
        Assert.Null(result.Credential?.ExpiresAt);
    }

    private static string CodexAuthJson(string accessToken) =>
        "{\"tokens\":{\"access_token\":\"" + accessToken + "\",\"account_id\":\"acct\"}}";

    // Minimal unsigned JWT carrying just an exp claim (signature is never validated).
    private static string FakeJwt(long expUnixSeconds)
    {
        static string Segment(string json) => Convert
            .ToBase64String(Encoding.UTF8.GetBytes(json))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return Segment("{\"alg\":\"none\",\"typ\":\"JWT\"}") + "."
            + Segment("{\"exp\":" + expUnixSeconds + "}") + ".sig";
    }

    // Like FakeJwt but lets the exp be any raw JSON number (e.g. out of DateTimeOffset range).
    private static string FakeJwtRawExp(string rawExp)
    {
        static string Segment(string json) => Convert
            .ToBase64String(Encoding.UTF8.GetBytes(json))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return Segment("{\"alg\":\"none\",\"typ\":\"JWT\"}") + "."
            + Segment("{\"exp\":" + rawExp + "}") + ".sig";
    }

    private CliCredentialSource Source() => new(() => _root, () => null);
    private void Write(string relative, string text) => WriteAt(Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar)), text);
    private static void WriteAt(string path, string text) { Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, text); }
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
