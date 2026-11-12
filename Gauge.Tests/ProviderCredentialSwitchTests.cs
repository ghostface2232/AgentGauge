using System.Net;
using System.Text;
using Gauge.Models;
using Gauge.Providers;
using Gauge.Providers.Internal;
using Gauge.Services;

namespace Gauge.Tests;

public sealed class ProviderCredentialSwitchTests
{
    [Fact]
    public async Task ClaudeAccountSwitchInvalidatesProviderCache()
    {
        var source = new MutableSource("token-one");
        var handler = new CountingHandler();
        var provider = new ClaudeProvider(new HttpClient(handler), source);
        await provider.GetSnapshotAsync(default);
        await provider.GetSnapshotAsync(default);
        Assert.Equal(1, handler.CallCount);

        source.Token = "token-two";
        await provider.GetSnapshotAsync(default);
        Assert.Equal(2, handler.CallCount);
    }

    [Fact]
    public async Task UnauthorizedUsageBecomesAuthenticationRequired()
    {
        var source = new MutableSource("expired");
        var handler = new CountingHandler(HttpStatusCode.Unauthorized);
        var provider = new ClaudeProvider(new HttpClient(handler), source);
        var error = await Assert.ThrowsAsync<AuthenticationRequiredException>(() => provider.GetSnapshotAsync(default));
        Assert.Equal(ToolKind.ClaudeCode, error.Tool);
    }

    [Fact]
    public async Task FailedUsageKeepsCacheAfterRotationButClearsItAfterAccountSwitch()
    {
        var source = new MutableSource("token-a") { Identity = "account-a" };
        var handler = new CountingHandler();
        using var http = new HttpClient(handler);
        var cache = new MemoryCache();
        using var coordinator = new UsageCoordinator(
            new UsageService([new ClaudeProvider(http, source)]), persistence: cache);
        UsageState? state = null;
        coordinator.Updated += (_, value) => state = value;
        await coordinator.RefreshAsync(RefreshReason.AuthenticationChanged);
        Assert.Equal("account-a", Assert.Single(cache.Snapshots).AccountIdentity);

        handler.Status = HttpStatusCode.ServiceUnavailable;
        source.Token = "rotated-a";
        await coordinator.RefreshAsync(RefreshReason.AuthenticationChanged);
        Assert.NotNull(Assert.Single(state!.Tools).Snapshot);

        source.Token = "token-b";
        source.Identity = "account-b";
        await coordinator.RefreshAsync(RefreshReason.AuthenticationChanged);
        Assert.Null(Assert.Single(state!.Tools).Snapshot);
        Assert.Empty(cache.Snapshots);
    }

    [Fact]
    public async Task SavesVerifiedPlanOnColdStartWhenUsageFails()
    {
        var captured = DateTimeOffset.UtcNow.AddDays(-1);
        var accountIdentity = UsageIdentity.Hash("account-a\0org-a", "previous-token");
        var cache = new MemoryCache
        {
            Snapshots = [new UsageSnapshot
            {
                ToolName = "Claude", Plan = "Pro", CapturedAt = captured, AccountIdentity = accountIdentity,
                Windows = [new UsageWindow { Type = UsageWindowType.Weekly, Label = "Weekly", UsedRatio = .2 }],
            }],
        };
        var handler = new CountingHandler(HttpStatusCode.TooManyRequests)
        {
            Profile = """{"account":{"uuid":"account-a"},"organization":{"uuid":"org-a","plan_display_name":"Ultra"}}""",
        };
        using var http = new HttpClient(handler);
        var source = new MutableSource("token") { Identity = "stale-local-profile" };
        using var coordinator = new UsageCoordinator(new UsageService(
            [new ClaudeProvider(http, new ClaudePlanSource(source, http))]), persistence: cache);
        UsageState? state = null;
        coordinator.Updated += (_, value) => state = value;
        await coordinator.RefreshAsync(RefreshReason.Periodic);

        var card = Assert.Single(state!.Tools);
        Assert.True(card.LastRefreshFailed);
        Assert.Equal("Ultra", card.Snapshot?.Plan);
        var stored = Assert.Single(cache.Snapshots);
        Assert.Equal(accountIdentity, stored.AccountIdentity);
        Assert.Equal("Ultra", stored.Plan);
        Assert.Equal(captured, stored.CapturedAt);
        Assert.Single(stored.Windows);
    }

    [Fact]
    public async Task CopilotPlanOnlyResponseClearsPriorAuthenticationFailure()
    {
        var handler = new CountingHandler(HttpStatusCode.Unauthorized) { Body = """{"copilot_plan":"individual"}""" };
        using var http = new HttpClient(handler);
        using var coordinator = new UsageCoordinator(
            new UsageService([new GitHubCopilotProvider(http, new MutableSource("token"))]));
        var authEvents = new List<bool>();
        coordinator.AuthenticationRequired += (_, _) => authEvents.Add(false);
        coordinator.AuthenticationRecovered += (_, _) => authEvents.Add(true);

        await coordinator.RefreshAsync(RefreshReason.Periodic);
        handler.Status = HttpStatusCode.OK;
        await coordinator.RefreshAsync(RefreshReason.AuthenticationChanged);

        Assert.Equal([false, true], authEvents);
    }

    private sealed class MutableSource(string token) : ICredentialSource
    {
        public string Token { get; set; } = token;
        public string? Identity { get; set; }
        public CredentialOwner Owner => CredentialOwner.CliLocal;
        public CredentialSource Source => CredentialSource.CliLocal;
        public Task<CredentialReadResult> ReadAsync(ToolKind tool, CancellationToken cancellationToken = default)
            => Task.FromResult(new CredentialReadResult
            {
                Tool = tool, Status = CredentialReadStatus.Available,
                Credential = new ToolCredential
                {
                    Tool = tool, Owner = Owner, Source = Source, AccessToken = Token, AccountIdentity = Identity,
                },
            });
    }

    private sealed class CountingHandler(HttpStatusCode statusCode = HttpStatusCode.OK) : HttpMessageHandler
    {
        public int CallCount { get; private set; }
        public HttpStatusCode Status { get; set; } = statusCode;
        public string Body { get; init; } = """{"five_hour":{"utilization":10}}""";
        public string? Profile { get; init; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            var isProfile = request.RequestUri?.AbsolutePath == "/api/oauth/profile";
            return Task.FromResult(new HttpResponseMessage(isProfile ? HttpStatusCode.OK : Status)
            {
                Content = new StringContent(isProfile ? Profile! : Body, Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class MemoryCache : IUsageCachePersistence
    {
        public IReadOnlyList<UsageSnapshot> Snapshots { get; set; } = [];
        public IReadOnlyList<UsageSnapshot> Load() => Snapshots;
        public void Save(IReadOnlyCollection<UsageSnapshot> snapshots) => Snapshots = snapshots.ToArray();
    }
}
