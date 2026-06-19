using System.Net;
using System.Text;
using Gauge.Models;
using Gauge.Providers;
using Gauge.Services;

namespace Gauge.Tests;

/// <summary>
/// JSON-schema tolerance and credential/auth handling for <see cref="ClaudeProvider"/>.
/// The throttle/cache (2 calls → 1 network hit), account-switch invalidation, and the
/// 401→auth-required path are already covered by <see cref="ProviderCredentialSwitchTests"/>,
/// so they are not repeated here. The time-dependent 429 escalation/cooldown-expiry is not
/// unit-testable without injecting a clock into the provider (see plan's deferred section).
/// </summary>
public sealed class ClaudeProviderTests
{
    [Fact]
    public async Task ParsesBothWindowsWithUtilizationAndReset()
    {
        const string json = """
        {
          "five_hour": { "utilization": 42, "resets_at": "2026-07-01T00:00:00Z" },
          "seven_day": { "utilization": 80 }
        }
        """;
        var snapshot = await Snapshot(json, Available("claude-token", "Max 5x"));

        Assert.Equal("Max 5x", snapshot.Plan);
        Assert.Equal(2, snapshot.Windows.Count);

        var fiveHour = Assert.Single(snapshot.Windows, w => w.Type == UsageWindowType.FiveHour);
        Assert.Equal(0.42, fiveHour.UsedRatio, 3);
        Assert.NotNull(fiveHour.ResetTime);

        var weekly = Assert.Single(snapshot.Windows, w => w.Type == UsageWindowType.Weekly);
        Assert.Equal(0.80, weekly.UsedRatio, 3);
        Assert.Null(weekly.ResetTime);
    }

    [Fact]
    public async Task MissingWeeklyWindowYieldsOnlyFiveHour()
    {
        var snapshot = await Snapshot("""{ "five_hour": { "utilization": 10 } }""", Available("t"));
        var window = Assert.Single(snapshot.Windows);
        Assert.Equal(UsageWindowType.FiveHour, window.Type);
    }

    [Theory]
    [InlineData("""{ "five_hour": { "resets_at": "2026-07-01T00:00:00Z" } }""")] // utilization absent
    [InlineData("""{ "five_hour": { "utilization": "oops" } }""")]              // wrong type
    [InlineData("{}")]                                                            // empty object
    [InlineData("""{ "unexpected": true, "five_hour": null }""")]               // unknown / null
    public async Task TolerantOfSchemaDrift(string json)
    {
        var snapshot = await Snapshot(json, Available("t"));
        Assert.Empty(snapshot.Windows);
    }

    [Theory]
    [InlineData(150.0, 1.0)]
    [InlineData(-5.0, 0.0)]
    public async Task UtilizationIsClampedToUnitRange(double utilization, double expected)
    {
        var json = $$"""{ "five_hour": { "utilization": {{utilization}} } }""";
        var snapshot = await Snapshot(json, Available("t"));
        var window = Assert.Single(snapshot.Windows);
        Assert.Equal(expected, window.UsedRatio, 3);
    }

    [Fact]
    public async Task InvalidCredentialBecomesAuthenticationRequired()
    {
        var provider = new ClaudeProvider(new HttpClient(new StubHandler("{}")), Invalid());
        var error = await Assert.ThrowsAsync<AuthenticationRequiredException>(
            () => provider.GetSnapshotAsync(default));
        Assert.Equal(ToolKind.ClaudeCode, error.Tool);
    }

    [Fact]
    public async Task MissingTokenYieldsEmptySnapshotWithoutThrowing()
    {
        var provider = new ClaudeProvider(new HttpClient(new StubHandler("{}")), Missing());
        var snapshot = await provider.GetSnapshotAsync(default);
        Assert.Empty(snapshot.Windows);
    }

    [Fact]
    public async Task PlanIsSurfacedEvenWhenUsageBodyIsEmpty()
    {
        // The plan comes from the credentials file, so it shows before any window data.
        var snapshot = await Snapshot("{}", Available("token", "Max 20x"));
        Assert.Equal("Max 20x", snapshot.Plan);
        Assert.Empty(snapshot.Windows);
    }

    [Fact]
    public async Task ColdStart429PropagatesWhenNothingCached()
    {
        var provider = new ClaudeProvider(
            new HttpClient(new StubHandler("{}", HttpStatusCode.TooManyRequests)), Available("t"));
        await Assert.ThrowsAsync<HttpRequestException>(() => provider.GetSnapshotAsync(default));
    }

    private static async Task<UsageSnapshot> Snapshot(string json, ICredentialSource source)
    {
        var provider = new ClaudeProvider(new HttpClient(new StubHandler(json)), source);
        return await provider.GetSnapshotAsync(default);
    }

    private static ICredentialSource Available(string token, string? plan = null) => new StubSource(
        new CredentialReadResult
        {
            Tool = ToolKind.ClaudeCode, Status = CredentialReadStatus.Available,
            Credential = new ToolCredential
            {
                Tool = ToolKind.ClaudeCode, Owner = CredentialOwner.CliLocal, Source = CredentialSource.CliLocal,
                AccessToken = token, Plan = plan,
            },
        });

    private static ICredentialSource Missing() => new StubSource(
        new CredentialReadResult { Tool = ToolKind.ClaudeCode, Status = CredentialReadStatus.Missing });

    private static ICredentialSource Invalid() => new StubSource(
        new CredentialReadResult { Tool = ToolKind.ClaudeCode, Status = CredentialReadStatus.Invalid });

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
