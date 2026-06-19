using System.Net;
using System.Text;
using Gauge.Models;
using Gauge.Providers;
using Gauge.Services;

namespace Gauge.Tests;

/// <summary>
/// JSON-schema tolerance, plan mapping, and credential/auth handling for
/// <see cref="CodexProvider"/>. Codex hits the same endpoint the CLI polls every 60s, so
/// it has no throttle/cache of its own; network/API failures must propagate rather than
/// collapse into an empty success (so the coordinator keeps the last good snapshot).
/// </summary>
public sealed class CodexProviderTests
{
    [Fact]
    public async Task ParsesPrimaryAndSecondaryWindows()
    {
        const string json = """
        {
          "plan_type": "pro",
          "rate_limit": {
            "primary_window": { "used_percent": 50, "reset_at": 1790000000 },
            "secondary_window": { "used_percent": 12 }
          }
        }
        """;
        var snapshot = await Snapshot(json, Available("codex-token", "acct"));

        Assert.Equal("Pro", snapshot.Plan);
        Assert.Equal(2, snapshot.Windows.Count);

        var fiveHour = Assert.Single(snapshot.Windows, w => w.Type == UsageWindowType.FiveHour);
        Assert.Equal(0.50, fiveHour.UsedRatio, 3);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1790000000), fiveHour.ResetTime);

        var weekly = Assert.Single(snapshot.Windows, w => w.Type == UsageWindowType.Weekly);
        Assert.Null(weekly.ResetTime);
    }

    [Theory]
    [InlineData("plus", "Plus")]
    [InlineData("go", "Go")]
    [InlineData("business", "Business")]
    [InlineData("startup", "Startup")] // unknown → title-cased
    [InlineData("", null)]
    public async Task MapsPlanType(string planType, string? expected)
    {
        var json = $$"""{ "plan_type": "{{planType}}", "rate_limit": {} }""";
        var snapshot = await Snapshot(json, Available("t"));
        Assert.Equal(expected, snapshot.Plan);
    }

    [Theory]
    [InlineData("{}")]                                                            // no rate_limit
    [InlineData("""{ "rate_limit": { "primary_window": { "reset_at": 1 } } }""")] // used_percent absent
    [InlineData("""{ "rate_limit": { "primary_window": { "used_percent": "x" } } }""")] // wrong type
    public async Task TolerantOfSchemaDrift(string json)
    {
        var snapshot = await Snapshot(json, Available("t"));
        Assert.Empty(snapshot.Windows);
    }

    [Fact]
    public async Task UsedPercentIsClamped()
    {
        var snapshot = await Snapshot(
            """{ "rate_limit": { "primary_window": { "used_percent": 250 } } }""", Available("t"));
        var window = Assert.Single(snapshot.Windows);
        Assert.Equal(1.0, window.UsedRatio, 3);
    }

    [Fact]
    public async Task MissingTokenYieldsEmptySnapshot()
    {
        var provider = new CodexProvider(new HttpClient(new StubHandler("{}")), Missing());
        var snapshot = await provider.GetSnapshotAsync(default);
        Assert.Empty(snapshot.Windows);
        Assert.Null(snapshot.Plan);
    }

    [Fact]
    public async Task InvalidCredentialBecomesAuthenticationRequired()
    {
        var provider = new CodexProvider(new HttpClient(new StubHandler("{}")), Invalid());
        var error = await Assert.ThrowsAsync<AuthenticationRequiredException>(
            () => provider.GetSnapshotAsync(default));
        Assert.Equal(ToolKind.Codex, error.Tool);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task UnauthorizedResponseBecomesAuthenticationRequired(HttpStatusCode code)
    {
        var provider = new CodexProvider(new HttpClient(new StubHandler("{}", code)), Available("t"));
        var error = await Assert.ThrowsAsync<AuthenticationRequiredException>(
            () => provider.GetSnapshotAsync(default));
        Assert.Equal(ToolKind.Codex, error.Tool);
    }

    [Fact]
    public async Task ServerErrorPropagatesInsteadOfEmptySuccess()
    {
        var provider = new CodexProvider(
            new HttpClient(new StubHandler("{}", HttpStatusCode.InternalServerError)), Available("t"));
        await Assert.ThrowsAsync<HttpRequestException>(() => provider.GetSnapshotAsync(default));
    }

    private static async Task<UsageSnapshot> Snapshot(string json, ICredentialSource source)
    {
        var provider = new CodexProvider(new HttpClient(new StubHandler(json)), source);
        return await provider.GetSnapshotAsync(default);
    }

    private static ICredentialSource Available(string token, string? accountId = null) => new StubSource(
        new CredentialReadResult
        {
            Tool = ToolKind.Codex, Status = CredentialReadStatus.Available,
            Credential = new ToolCredential
            {
                Tool = ToolKind.Codex, Owner = CredentialOwner.CliLocal, Source = CredentialSource.CliLocal,
                AccessToken = token, AccountId = accountId,
            },
        });

    private static ICredentialSource Missing() => new StubSource(
        new CredentialReadResult { Tool = ToolKind.Codex, Status = CredentialReadStatus.Missing });

    private static ICredentialSource Invalid() => new StubSource(
        new CredentialReadResult { Tool = ToolKind.Codex, Status = CredentialReadStatus.Invalid });

    private sealed class StubSource(CredentialReadResult result) : ICredentialSource
    {
        public CredentialOwner Owner => CredentialOwner.CliLocal;
        public CredentialSource Source => CredentialSource.CliLocal;
        public Task<CredentialReadResult> ReadAsync(ToolKind tool, CancellationToken cancellationToken = default)
            => Task.FromResult(result);
    }

    private sealed class StubHandler(string json, HttpStatusCode statusCode = HttpStatusCode.OK) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            });
    }
}
