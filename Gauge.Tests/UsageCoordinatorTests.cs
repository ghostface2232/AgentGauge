using System.Net;
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
    public async Task RaisesAuthenticationRequiredOnAuthErrorThenRecoveredOnSuccess()
    {
        var provider = new StubProvider("Codex");
        using var coordinator = new UsageCoordinator(new UsageService(new[] { provider }));
        var required = new List<ToolKind>();
        var recovered = new List<ToolKind>();
        coordinator.AuthenticationRequired += (_, tool) => required.Add(tool);
        coordinator.AuthenticationRecovered += (_, tool) => recovered.Add(tool);

        provider.ThrowAuth = true;
        await coordinator.RefreshAsync(RefreshReason.AuthenticationChanged);
        Assert.Equal(new[] { ToolKind.Codex }, required);
        Assert.Empty(recovered);

        // A later success raises Recovered so a sticky rejection can be cleared.
        provider.ThrowAuth = false;
        await coordinator.RefreshAsync(RefreshReason.AuthenticationChanged);
        Assert.Equal(new[] { ToolKind.Codex }, recovered);
    }

    [Fact]
    public async Task EmptySnapshotSuccessDoesNotRaiseRecovered()
    {
        // No credential → providers return an empty snapshot as a "success"; that is NOT a
        // server acceptance, so it must not clear a sticky rejection.
        var provider = new StubProvider("Codex") { EmptyWindows = true };
        using var coordinator = new UsageCoordinator(new UsageService(new[] { provider }));
        var recovered = new List<ToolKind>();
        coordinator.AuthenticationRecovered += (_, tool) => recovered.Add(tool);

        await coordinator.RefreshAsync(RefreshReason.AuthenticationChanged);

        Assert.Empty(recovered);
    }

    [Fact]
    public async Task ReservedCacheWithUnchangedCaptureTimeDoesNotRaiseRecovered()
    {
        // Simulate a provider re-serving its last good snapshot (same CapturedAt) on a
        // cooldown/429/network error: only the first, genuinely live fetch recovers.
        var provider = new StubProvider("Codex") { FixedCapturedAt = DateTimeOffset.UtcNow };
        using var coordinator = new UsageCoordinator(new UsageService(new[] { provider }));
        var recovered = new List<ToolKind>();
        coordinator.AuthenticationRecovered += (_, tool) => recovered.Add(tool);

        await coordinator.RefreshAsync(RefreshReason.AuthenticationChanged);
        Assert.Equal(new[] { ToolKind.Codex }, recovered); // first fetch is live

        recovered.Clear();
        await coordinator.RefreshAsync(RefreshReason.AuthenticationChanged);
        Assert.Empty(recovered); // re-served same snapshot → not a fresh acceptance
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

    [Fact]
    public async Task RehydratedSnapshotIsShownWhenColdStartRefreshFails()
    {
        var persistence = new FakePersistence
        {
            Seed = new[]
            {
                new UsageSnapshot
                {
                    ToolName = "Claude Code",
                    CapturedAt = DateTimeOffset.UtcNow.AddMinutes(-20),
                    Windows = new[] { new UsageWindow { Type = UsageWindowType.FiveHour, Label = "5h", UsedRatio = .5 } },
                },
            },
        };
        var provider = new StubProvider("Claude Code") { Throw = true };
        using var coordinator = new UsageCoordinator(new UsageService(new[] { provider }), persistence: persistence);
        UsageState? state = null;
        coordinator.Updated += (_, value) => state = value;

        await coordinator.RefreshAsync(RefreshReason.AuthenticationChanged);

        // Even though the live fetch failed on cold start, the rehydrated value is surfaced.
        var claude = Assert.Single(state!.Tools);
        Assert.NotNull(claude.Snapshot);
        Assert.True(claude.LastRefreshFailed);
    }

    [Fact]
    public async Task SuccessfulRefreshIsPersisted()
    {
        var persistence = new FakePersistence();
        var provider = new StubProvider("Codex");
        using var coordinator = new UsageCoordinator(new UsageService(new[] { provider }), persistence: persistence);

        await coordinator.RefreshAsync(RefreshReason.Manual);

        var saved = Assert.Single(persistence.Saved);
        Assert.Equal("Codex", saved.ToolName);
    }

    [Fact]
    public async Task AllFailedCycleDoesNotOverwritePersistedCache()
    {
        var persistence = new FakePersistence();
        var provider = new StubProvider("Codex") { Throw = true };
        using var coordinator = new UsageCoordinator(new UsageService(new[] { provider }), persistence: persistence);

        await coordinator.RefreshAsync(RefreshReason.AuthenticationChanged);

        Assert.False(persistence.SaveCalled);
    }

    [Fact]
    public async Task PurgingAToolRewritesCacheEvenWithNoSuccessfulProvider()
    {
        var persistence = new FakePersistence
        {
            Seed = new[]
            {
                Seed("Claude Code"),
                Seed("Codex"),
            },
        };
        var claude = new StubProvider("Claude Code") { Throw = true };
        var codex = new StubProvider("Codex") { Throw = true };
        var enabled = new HashSet<ToolKind> { ToolKind.ClaudeCode, ToolKind.Codex };
        using var coordinator = new UsageCoordinator(
            new UsageService(new IUsageProvider[] { claude, codex }, enabled.Contains), persistence: persistence);

        // All providers fail and nothing was purged → the disk cache is left untouched.
        await coordinator.RefreshAsync(RefreshReason.AuthenticationChanged);
        Assert.False(persistence.SaveCalled);

        // Remove a tool: even though the remaining provider still fails, the purge must be
        // flushed to disk so the removed tool can't reappear on next launch.
        enabled.Remove(ToolKind.Codex);
        await coordinator.RefreshAsync(RefreshReason.ToolsChanged);

        Assert.True(persistence.SaveCalled);
        var saved = Assert.Single(persistence.Saved);
        Assert.Equal("Claude Code", saved.ToolName);
    }

    private static UsageSnapshot Seed(string name) => new()
    {
        ToolName = name,
        CapturedAt = DateTimeOffset.UtcNow.AddMinutes(-10),
        Windows = new[] { new UsageWindow { Type = UsageWindowType.FiveHour, Label = "5h", UsedRatio = .3 } },
    };

    private sealed class FakePersistence : IUsageCachePersistence
    {
        public IReadOnlyList<UsageSnapshot> Seed { get; set; } = Array.Empty<UsageSnapshot>();
        public IReadOnlyList<UsageSnapshot> Saved { get; private set; } = Array.Empty<UsageSnapshot>();
        public bool SaveCalled { get; private set; }

        public IReadOnlyList<UsageSnapshot> Load() => Seed;
        public void Save(IReadOnlyCollection<UsageSnapshot> snapshots)
        {
            SaveCalled = true;
            Saved = snapshots.ToList();
        }
    }

    private sealed class StubProvider(string name) : IUsageProvider
    {
        public ToolKind Tool => name == "Codex" ? ToolKind.Codex : ToolKind.ClaudeCode;
        public string ToolName => name;
        public int CallCount { get; private set; }
        public bool Throw { get; set; }
        public bool ThrowAuth { get; set; }
        public bool EmptyWindows { get; set; }
        public DateTimeOffset? FixedCapturedAt { get; set; }
        public Task<UsageSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
        {
            CallCount++;
            if (ThrowAuth) throw new AuthenticationRequiredException(Tool, HttpStatusCode.Unauthorized);
            if (Throw) throw new HttpRequestException("offline");
            var windows = EmptyWindows
                ? Array.Empty<UsageWindow>()
                : new[] { new UsageWindow { Type = UsageWindowType.FiveHour, Label = "5시간", UsedRatio = .2 } };
            return Task.FromResult(new UsageSnapshot
            {
                ToolName = ToolName,
                CapturedAt = FixedCapturedAt ?? DateTimeOffset.Now,
                Windows = windows,
            });
        }
    }
}
