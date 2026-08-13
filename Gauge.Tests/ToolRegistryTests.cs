using Gauge.Models;
using Gauge.Services;

namespace Gauge.Tests;

public sealed class ToolRegistryTests
{
    [Fact]
    public void DefaultsToClaudeAndCodexWhenNothingStored()
    {
        var registry = new ToolRegistry(new InMemoryStore());

        Assert.True(registry.IsEnabled(ToolKind.ClaudeCode));
        Assert.True(registry.IsEnabled(ToolKind.Codex));
        Assert.False(registry.IsEnabled(ToolKind.Cursor));
    }

    [Fact]
    public void EnabledAndAvailableFollowCatalogOrder()
    {
        var registry = new ToolRegistry(new InMemoryStore());

        Assert.Equal(new[] { ToolKind.ClaudeCode, ToolKind.Codex }, registry.Enabled);
        Assert.Equal(new[] { ToolKind.Cursor, ToolKind.Antigravity, ToolKind.GitHubCopilot }, registry.Available);
    }

    [Fact]
    public void AddMovesToolFromAvailableToEnabledAndRaisesChanged()
    {
        var registry = new ToolRegistry(new InMemoryStore());
        var changed = 0;
        registry.Changed += (_, _) => changed++;

        Assert.True(registry.Add(ToolKind.Cursor));
        Assert.True(registry.IsEnabled(ToolKind.Cursor));
        Assert.DoesNotContain(ToolKind.Cursor, registry.Available);
        Assert.Equal(1, changed);

        // Adding again is a no-op (no event, returns false).
        Assert.False(registry.Add(ToolKind.Cursor));
        Assert.Equal(1, changed);
    }

    [Fact]
    public void RemoveDisablesToolAndRaisesChanged()
    {
        var registry = new ToolRegistry(new InMemoryStore());
        var changed = 0;
        registry.Changed += (_, _) => changed++;

        Assert.True(registry.Remove(ToolKind.Codex));
        Assert.False(registry.IsEnabled(ToolKind.Codex));
        Assert.Contains(ToolKind.Codex, registry.Available);
        Assert.Equal(1, changed);

        Assert.False(registry.Remove(ToolKind.Codex));
        Assert.Equal(1, changed);
    }

    [Fact]
    public void ReorderEnabledRaisesOrderChangedNotChangedAndPersists()
    {
        var store = new InMemoryStore();
        var registry = new ToolRegistry(store);
        var changed = 0;
        var ordered = 0;
        registry.Changed += (_, _) => changed++;
        registry.OrderChanged += (_, _) => ordered++;

        Assert.True(registry.ReorderEnabled(new[] { ToolKind.Codex, ToolKind.ClaudeCode }));
        Assert.Equal(new[] { ToolKind.Codex, ToolKind.ClaudeCode }, registry.Enabled);
        // Reorder must NOT raise Changed (which would trigger a usage re-fetch) — only OrderChanged.
        Assert.Equal(0, changed);
        Assert.Equal(1, ordered);

        // The new order survives a reload from the same store.
        var reloaded = new ToolRegistry(store);
        Assert.Equal(new[] { ToolKind.Codex, ToolKind.ClaudeCode }, reloaded.Enabled);
    }

    [Fact]
    public void ReorderEnabledIsNoOpWhenOrderUnchanged()
    {
        var registry = new ToolRegistry(new InMemoryStore());
        var ordered = 0;
        registry.OrderChanged += (_, _) => ordered++;

        Assert.False(registry.ReorderEnabled(new[] { ToolKind.ClaudeCode, ToolKind.Codex }));
        Assert.Equal(0, ordered);
    }

    [Fact]
    public void ReorderEnabledWithSubsetKeepsToolsNotInTheSubsetPinned()
    {
        // Three enabled tools; reorder only the two the main screen shows (a subset).
        var store = new InMemoryStore();
        var registry = new ToolRegistry(store);
        registry.Add(ToolKind.Cursor); // order: Claude, Codex, Cursor

        // Swap Claude and Cursor; Codex is not in the subset, so it stays in its slot (index 1).
        Assert.True(registry.ReorderEnabled(new[] { ToolKind.Cursor, ToolKind.ClaudeCode }));
        Assert.Equal(new[] { ToolKind.Cursor, ToolKind.Codex, ToolKind.ClaudeCode }, registry.Enabled);
    }

    [Fact]
    public void ChangesPersistAcrossInstances()
    {
        var store = new InMemoryStore();
        var first = new ToolRegistry(store);
        first.Add(ToolKind.Cursor);
        first.Remove(ToolKind.Codex);

        var second = new ToolRegistry(store);
        Assert.True(second.IsEnabled(ToolKind.ClaudeCode));
        Assert.True(second.IsEnabled(ToolKind.Cursor));
        Assert.False(second.IsEnabled(ToolKind.Codex));
    }

    [Fact]
    public void FileStoreRoundTripsThroughDisk()
    {
        var dir = Path.Combine(Path.GetTempPath(), "GaugeToolRegistryTest_" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new ToolRegistryStore(() => dir);
            // Default when no file exists.
            var initial = store.Load();
            Assert.Equal(2, initial.Count);
            Assert.Contains(ToolKind.ClaudeCode, initial);
            Assert.Contains(ToolKind.Codex, initial);

            store.Save(new[] { ToolKind.ClaudeCode, ToolKind.Cursor });
            var reloaded = store.Load();
            Assert.Contains(ToolKind.ClaudeCode, reloaded);
            Assert.Contains(ToolKind.Cursor, reloaded);
            Assert.DoesNotContain(ToolKind.Codex, reloaded);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void EnabledSnapshotIsImmutableUnderLaterMutations()
    {
        // The coordinator's refresh loop reads Enabled/IsEnabled from a thread-pool
        // thread while the UI thread mutates the registry, so a returned snapshot must
        // never change under the reader's feet (copy-on-write contract).
        var registry = new ToolRegistry(new InMemoryStore());
        var snapshot = registry.Enabled;

        registry.Add(ToolKind.Cursor);
        registry.Remove(ToolKind.Codex);
        registry.ReorderEnabled(new[] { ToolKind.Cursor, ToolKind.ClaudeCode });

        Assert.Equal(new[] { ToolKind.ClaudeCode, ToolKind.Codex }, snapshot);
    }

    [Fact]
    public async Task ConcurrentReadsDuringMutationsNeverThrow()
    {
        // Regression smoke for the torn-read race: hammer cross-thread reads (as the
        // refresh loop does) while the "UI thread" adds/removes/reorders. With in-place
        // List mutation this could observe a mid-resize state and throw or misreport.
        var registry = new ToolRegistry(new InMemoryStore());
        using var stop = new CancellationTokenSource();
        var reader = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                foreach (var kind in registry.Enabled)
                {
                    Assert.True(Enum.IsDefined(kind));
                }
                _ = registry.IsEnabled(ToolKind.Cursor);
                _ = registry.Available;
            }
        });

        for (var i = 0; i < 2000; i++)
        {
            registry.Add(ToolKind.Cursor);
            registry.ReorderEnabled(new[] { ToolKind.Cursor, ToolKind.ClaudeCode });
            registry.Remove(ToolKind.Cursor);
        }

        stop.Cancel();
        await reader;
    }

    /// <summary>In-memory store seeded with the production default.</summary>
    private sealed class InMemoryStore : IToolRegistryStore
    {
        private IReadOnlyCollection<ToolKind> _state = new[] { ToolKind.ClaudeCode, ToolKind.Codex };
        public IReadOnlyCollection<ToolKind> Load() => _state;
        public void Save(IReadOnlyCollection<ToolKind> enabled) => _state = enabled.ToList();
    }
}
