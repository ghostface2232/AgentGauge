using System.Net;
using Gauge.Models;
using Gauge.Providers;
using Gauge.Services;

namespace Gauge.Tests;

public sealed class UsageCoordinatorTests
{
    [Theory]
    [InlineData(ToolKind.Codex, 3)]
    [InlineData(ToolKind.ClaudeCode, 5)]
    [InlineData(ToolKind.Cursor, 5)]
    [InlineData(ToolKind.Antigravity, 10)]
    [InlineData(ToolKind.GitHubCopilot, 15)]
    public void BackgroundCadenceMatchesProviderCostAndWindowShape(ToolKind tool, int minutes)
    {
        Assert.Equal(TimeSpan.FromMinutes(minutes), UsageCoordinator.PeriodicIntervalFor(tool));
    }

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
    public async Task DisablingFinalToolPurgesCacheWithoutAProviderCall()
    {
        var provider = new StubProvider("Codex");
        var enabled = new HashSet<ToolKind> { ToolKind.Codex };
        using var coordinator = new UsageCoordinator(
            new UsageService(new[] { provider }, enabled.Contains));
        UsageState? state = null;
        coordinator.Updated += (_, value) => state = value;

        await coordinator.RefreshAsync(RefreshReason.ToolsChanged);
        Assert.Single(state!.Tools);

        enabled.Clear();
        await coordinator.RefreshAsync(RefreshReason.ToolsChanged);

        Assert.Empty(state!.Tools);
        Assert.Equal(1, provider.CallCount);
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

    [Theory]
    [InlineData(ToolKind.Antigravity, 2)]
    [InlineData(ToolKind.Codex, 0)]
    [InlineData(ToolKind.ClaudeCode, 0)]
    [InlineData(ToolKind.Cursor, 0)]
    [InlineData(ToolKind.GitHubCopilot, 0)]
    public void OnlyProcessSpawningProvidersHaveAPopoverOpenCostFloor(ToolKind tool, int minutes)
    {
        Assert.Equal(TimeSpan.FromMinutes(minutes), UsageCoordinator.ForcedIntervalFor(tool));
    }

    [Fact]
    public async Task PopoverOpenSkipsAProviderInsideItsCostFloorButStillShowsItsCard()
    {
        // Antigravity has no endpoint when the IDE is closed: a read launches a language
        // server and tears it down again, so every tray click used to spawn one. The floor
        // outlives the 10s debounce, so re-opening the popover a minute later still skips it.
        var time = new MutableTime();
        var antigravity = new StubProvider("Antigravity");
        using var coordinator = new UsageCoordinator(new UsageService(new[] { antigravity }), time: time);
        UsageState? state = null;
        coordinator.Updated += (_, value) => state = value;

        await coordinator.RefreshAsync(RefreshReason.PopoverOpened);
        Assert.Equal(1, antigravity.CallCount);

        time.Advance(TimeSpan.FromMinutes(1)); // past the debounce, inside the two-minute floor
        state = null;
        await coordinator.RefreshAsync(RefreshReason.PopoverOpened);

        Assert.Equal(1, antigravity.CallCount);
        // Nothing was fetched, but the popover must still receive the cached card.
        Assert.NotNull(state);
        Assert.NotNull(Assert.Single(state!.Tools).Snapshot);
    }

    [Fact]
    public async Task PopoverOpenRefreshesAgainOnceTheCostFloorHasPassed()
    {
        var time = new MutableTime();
        var antigravity = new StubProvider("Antigravity");
        using var coordinator = new UsageCoordinator(new UsageService(new[] { antigravity }), time: time);

        await coordinator.RefreshAsync(RefreshReason.PopoverOpened);
        time.Advance(TimeSpan.FromMinutes(3));
        await coordinator.RefreshAsync(RefreshReason.PopoverOpened);

        Assert.Equal(2, antigravity.CallCount);
    }

    [Fact]
    public async Task ExplicitUserActionsBypassTheCostFloor()
    {
        var time = new MutableTime();
        var antigravity = new StubProvider("Antigravity");
        using var coordinator = new UsageCoordinator(new UsageService(new[] { antigravity }), time: time);

        await coordinator.RefreshAsync(RefreshReason.PopoverOpened);
        // A completed sign-in and adding a tool both mean "now, really".
        await coordinator.RefreshAsync(RefreshReason.AuthenticationChanged);
        await coordinator.RefreshAsync(RefreshReason.ToolsChanged);
        // So does the refresh button, once past the shared 10-second debounce.
        time.Advance(TimeSpan.FromSeconds(11));
        await coordinator.RefreshAsync(RefreshReason.Manual);

        Assert.Equal(4, antigravity.CallCount);
    }

    [Fact]
    public async Task CostFlooredProviderDoesNotBlockAnotherProvidersRefresh()
    {
        var time = new MutableTime();
        var antigravity = new StubProvider("Antigravity");
        var codex = new StubProvider("Codex");
        using var coordinator = new UsageCoordinator(
            new UsageService(new IUsageProvider[] { antigravity, codex }), time: time);

        await coordinator.RefreshAsync(RefreshReason.PopoverOpened);
        time.Advance(TimeSpan.FromMinutes(1));
        await coordinator.RefreshAsync(RefreshReason.PopoverOpened);

        Assert.Equal(1, antigravity.CallCount);
        Assert.Equal(2, codex.CallCount);
    }

    [Fact]
    public async Task ForcedRefreshWithinTheDebounceIsSkippedRegardlessOfTheFloor()
    {
        // The two gates are independent: the debounce covers rapid clicking for every
        // provider, the floor covers the expensive one over a longer span.
        var time = new MutableTime();
        var codex = new StubProvider("Codex");
        using var coordinator = new UsageCoordinator(new UsageService(new[] { codex }), time: time);

        await coordinator.RefreshAsync(RefreshReason.PopoverOpened);
        time.Advance(TimeSpan.FromSeconds(9));
        await coordinator.RefreshAsync(RefreshReason.PopoverOpened);
        Assert.Equal(1, codex.CallCount);

        time.Advance(TimeSpan.FromSeconds(2));
        await coordinator.RefreshAsync(RefreshReason.PopoverOpened);
        Assert.Equal(2, codex.CallCount);
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

    /// <summary>
    /// Drives the coordinator's monotonic gates (the forced-refresh debounce and the
    /// per-provider cost floor) without real waiting. Starts at a non-zero timestamp so a
    /// test can never accidentally depend on the "never refreshed" sentinel being zero.
    /// </summary>
    private sealed class MutableTime : TimeProvider
    {
        private long _timestamp = TimeSpan.TicksPerHour;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => _timestamp;

        public void Advance(TimeSpan elapsed) => _timestamp += elapsed.Ticks;
    }

    private sealed class StubProvider(string name) : IUsageProvider
    {
        public ToolKind Tool => name switch
        {
            "Codex" => ToolKind.Codex,
            "Antigravity" => ToolKind.Antigravity,
            _ => ToolKind.ClaudeCode,
        };
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
