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

    [Fact]
    public async Task ColdStartFailureLeavesNullSnapshotAndNoLastUpdated()
    {
        var provider = new StubProvider("Codex") { Throw = true };
        using var coordinator = new UsageCoordinator(new UsageService(new[] { provider }));
        UsageState? state = null;
        coordinator.Updated += (_, value) => state = value;

        await coordinator.RefreshAsync(RefreshReason.AuthenticationChanged);

        var codex = Assert.Single(state!.Tools);
        Assert.Null(codex.Snapshot);
        Assert.True(codex.LastRefreshFailed);
        Assert.Null(state.LastUpdatedAt);
    }

    [Fact]
    public async Task SuccessAfterFailureClearsTheFailedFlag()
    {
        var provider = new StubProvider("Codex") { Throw = true };
        using var coordinator = new UsageCoordinator(new UsageService(new[] { provider }));
        UsageState? state = null;
        coordinator.Updated += (_, value) => state = value;

        await coordinator.RefreshAsync(RefreshReason.AuthenticationChanged);
        provider.Throw = false;
        await coordinator.RefreshAsync(RefreshReason.AuthenticationChanged);

        var codex = Assert.Single(state!.Tools);
        Assert.NotNull(codex.Snapshot);
        Assert.False(codex.LastRefreshFailed);
        Assert.NotNull(state.LastUpdatedAt);
    }

    [Fact]
    public async Task DisablingAToolPurgesItFromTheCache()
    {
        var claude = new StubProvider("Claude Code");
        var codex = new StubProvider("Codex");
        var enabled = new HashSet<ToolKind> { ToolKind.ClaudeCode, ToolKind.Codex };
        using var coordinator = new UsageCoordinator(
            new UsageService(new IUsageProvider[] { claude, codex }, enabled.Contains));
        UsageState? state = null;
        coordinator.Updated += (_, value) => state = value;

        await coordinator.RefreshAsync(RefreshReason.ToolsChanged);
        Assert.Equal(2, state!.Tools.Count);

        enabled.Remove(ToolKind.Codex);
        await coordinator.RefreshAsync(RefreshReason.ToolsChanged);

        var remaining = Assert.Single(state!.Tools);
        Assert.Equal("Claude Code", remaining.ToolName);
    }

    [Fact]
    public async Task PopoverOpenWithinDebounceServesCacheWithoutRefetching()
    {
        var provider = new StubProvider("Codex");
        using var coordinator = new UsageCoordinator(new UsageService(new[] { provider }));

        await coordinator.RefreshAsync(RefreshReason.PopoverOpened);
        await coordinator.RefreshAsync(RefreshReason.PopoverOpened);

        Assert.Equal(1, provider.CallCount);
    }

    private sealed class StubProvider(string name) : IUsageProvider
    {
        public ToolKind Tool => name == "Codex" ? ToolKind.Codex : ToolKind.ClaudeCode;
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
