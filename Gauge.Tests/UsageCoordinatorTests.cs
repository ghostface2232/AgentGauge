using Gauge.Models;
using Gauge.Providers;
using Gauge.Services;

namespace Gauge.Tests;

public sealed class UsageCoordinatorTests
{
    [Fact]
    public async Task AuthenticationRefreshBypassesManualDebounce()
    {
        var provider = new StubProvider("Codex");
        using var coordinator = new UsageCoordinator(new UsageService(new[] { provider }));
        await coordinator.RefreshAsync(RefreshReason.Manual);
        await coordinator.RefreshAsync(RefreshReason.Manual);
        await coordinator.RefreshAsync(RefreshReason.AuthenticationChanged);
        Assert.Equal(2, provider.CallCount);
    }

    [Fact]
    public async Task ProviderFailureIsIsolatedAndLastSnapshotIsRetained()
    {
        var good = new StubProvider("Claude Code");
        var flaky = new StubProvider("Codex");
        using var coordinator = new UsageCoordinator(new UsageService(new IUsageProvider[] { good, flaky }));
        UsageState? state = null;
        coordinator.Updated += (_, value) => state = value;
        await coordinator.RefreshAsync(RefreshReason.AuthenticationChanged);
        flaky.Throw = true;
        await coordinator.RefreshAsync(RefreshReason.AuthenticationChanged);

        Assert.Equal(2, good.CallCount);
        var codex = Assert.Single(state!.Tools, tool => tool.ToolName == "Codex");
        Assert.NotNull(codex.Snapshot);
        Assert.True(codex.LastRefreshFailed);
    }

    private sealed class StubProvider(string name) : IUsageProvider
    {
        public string ToolName => name;
        public int CallCount { get; private set; }
        public bool Throw { get; set; }
        public Task<UsageSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
        {
            CallCount++;
            if (Throw) throw new HttpRequestException("offline");
            return Task.FromResult(new UsageSnapshot { ToolName = ToolName, CapturedAt = DateTimeOffset.Now,
                Windows = new[] { new UsageWindow { Type = UsageWindowType.FiveHour, Label = "5시간", UsedRatio = .2 } } });
        }
    }
}
