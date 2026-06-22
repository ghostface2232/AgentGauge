using System.Net;
using System.Text;
using Gauge.Models;
using Gauge.Providers;
using Gauge.Services;

namespace Gauge.Tests;

public sealed class GitHubCopilotProviderTests
{
    // The real copilot_internal/user response shape captured from a live (free-tier) account:
    // chat/completions are metered, premium_interactions has no quota, monthly reset date.
    private const string LiveFreeJson = """
    {
      "login": "octocat",
      "access_type_sku": "free_limited_copilot",
      "copilot_plan": "individual",
      "quota_snapshots": {
        "chat":        { "percent_remaining": 100.0, "unlimited": false, "has_quota": true,  "remaining": 200,  "entitlement": 200 },
        "completions": { "percent_remaining": 100.0, "unlimited": false, "has_quota": true,  "remaining": 2000, "entitlement": 2000 },
        "premium_interactions": { "percent_remaining": 0.0, "unlimited": false, "has_quota": false, "remaining": 0, "entitlement": 0 }
      },
      "quota_reset_date_utc": "2026-07-01T00:00:00Z"
    }
    """;

    [Fact]
    public async Task ParsesMeteredQuotasIntoBillingCycleWindows()
    {
        var snapshot = await new GitHubCopilotProvider(new HttpClient(new StubHandler(LiveFreeJson)), TokenSource())
            .GetSnapshotAsync(default);

        Assert.Equal("Free", snapshot.Plan);
        // chat + completions are metered; premium_interactions (has_quota=false) is skipped.
        Assert.Equal(2, snapshot.Windows.Count);
        Assert.All(snapshot.Windows, w => Assert.Equal(UsageWindowType.BillingCycle, w.Type));
        Assert.All(snapshot.Windows, w => Assert.NotNull(w.ResetTime));

        var chat = Assert.Single(snapshot.Windows, w => w.Id == "chat");
        Assert.Equal(0.0, chat.UsedRatio, 3); // 100% remaining → 0% used
        Assert.DoesNotContain(snapshot.Windows, w => w.Id == "premium_interactions");
    }

    [Fact]
    public async Task ComputesUsedFromPercentRemaining()
    {
        const string json = """
        { "quota_snapshots": { "chat": { "percent_remaining": 25.0, "has_quota": true, "unlimited": false } } }
        """;
        var window = Assert.Single((await Snapshot(json)).Windows);
        Assert.Equal(0.75, window.UsedRatio, 3);
    }

    [Fact]
    public async Task FallsBackToRemainingOverEntitlementWhenNoPercent()
    {
        const string json = """
        { "quota_snapshots": { "premium_interactions": { "has_quota": true, "unlimited": false, "remaining": 75, "entitlement": 300 } } }
        """;
        var window = Assert.Single((await Snapshot(json)).Windows);
        Assert.Equal(0.75, window.UsedRatio, 3); // 1 - 75/300
    }

    [Fact]
    public async Task SkipsUnlimitedAndUnmeteredQuotas()
    {
        const string json = """
        {
          "quota_snapshots": {
            "completions": { "unlimited": true,  "has_quota": true,  "percent_remaining": 50 },
            "premium_interactions": { "unlimited": false, "has_quota": false, "percent_remaining": 50 },
            "chat": { "unlimited": false, "has_quota": true, "percent_remaining": 50 }
          }
        }
        """;
        var window = Assert.Single((await Snapshot(json)).Windows);
        Assert.Equal("chat", window.Id);
    }

    [Fact]
    public async Task PercentAboveHundredIsClamped()
    {
        const string json = """
        { "quota_snapshots": { "chat": { "percent_remaining": -50, "has_quota": true } } }
        """;
        var window = Assert.Single((await Snapshot(json)).Windows);
        Assert.Equal(1.0, window.UsedRatio, 3);
    }

    [Theory]
    [InlineData("individual", null, "Pro")]
    [InlineData("business", null, "Business")]
    [InlineData("enterprise", null, "Enterprise")]
    [InlineData("individual", "free_limited_copilot", "Free")]
    public async Task MapsPlan(string copilotPlan, string? sku, string expected)
    {
        var skuLine = sku is null ? "" : $"\"access_type_sku\": \"{sku}\",";
        var json = $$"""
        { {{skuLine}} "copilot_plan": "{{copilotPlan}}", "quota_snapshots": { "chat": { "percent_remaining": 10, "has_quota": true } } }
        """;
        Assert.Equal(expected, (await Snapshot(json)).Plan);
    }

    [Fact]
    public async Task SendsTokenAndEditorHeaders()
    {
        var handler = new StubHandler(LiveFreeJson);
        await new GitHubCopilotProvider(new HttpClient(handler), TokenSource("gho_secret")).GetSnapshotAsync(default);
        Assert.Equal("token gho_secret", handler.Authorization);
        Assert.NotNull(handler.EditorVersion);
    }

    [Fact]
    public async Task NotSignedInYieldsEmptySnapshot()
    {
        var provider = new GitHubCopilotProvider(new HttpClient(new StubHandler("{}")), MissingSource());
        Assert.Empty((await provider.GetSnapshotAsync(default)).Windows);
    }

    [Fact]
    public async Task UnauthorizedBecomesAuthenticationRequired()
    {
        var handler = new StubHandler("{}", HttpStatusCode.Unauthorized);
        var provider = new GitHubCopilotProvider(new HttpClient(handler), TokenSource());
        var error = await Assert.ThrowsAsync<AuthenticationRequiredException>(() => provider.GetSnapshotAsync(default));
        Assert.Equal(ToolKind.GitHubCopilot, error.Tool);
    }

    private static async Task<UsageSnapshot> Snapshot(string json)
        => await new GitHubCopilotProvider(new HttpClient(new StubHandler(json)), TokenSource()).GetSnapshotAsync(default);

    private static ICredentialSource TokenSource(string token = "gho_token") => new StubSource(
        new CredentialReadResult
        {
            Tool = ToolKind.GitHubCopilot, Status = CredentialReadStatus.Available,
            Credential = new ToolCredential
            {
                Tool = ToolKind.GitHubCopilot, Owner = CredentialOwner.CliLocal, Source = CredentialSource.CliLocal,
                AccessToken = token,
            },
        });

    private static ICredentialSource MissingSource() => new StubSource(
        new CredentialReadResult { Tool = ToolKind.GitHubCopilot, Status = CredentialReadStatus.Missing });

    private sealed class StubSource(CredentialReadResult result) : ICredentialSource
    {
        public CredentialOwner Owner => CredentialOwner.CliLocal;
        public CredentialSource Source => CredentialSource.CliLocal;
        public Task<CredentialReadResult> ReadAsync(ToolKind tool, CancellationToken cancellationToken = default)
            => Task.FromResult(result);
    }

    private sealed class StubHandler(string json, HttpStatusCode statusCode = HttpStatusCode.OK) : HttpMessageHandler
    {
        public string? Authorization { get; private set; }
        public string? EditorVersion { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Authorization = request.Headers.TryGetValues("Authorization", out var auth) ? string.Join("; ", auth) : null;
            EditorVersion = request.Headers.TryGetValues("Editor-Version", out var ev) ? string.Join("; ", ev) : null;
            return Task.FromResult(new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            });
        }
    }
}
