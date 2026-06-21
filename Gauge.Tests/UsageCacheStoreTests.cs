using Gauge.Models;
using Gauge.Services;

namespace Gauge.Tests;

/// <summary>Round-trips the on-disk last-known usage cache.</summary>
public sealed class UsageCacheStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "GaugeCacheTest_" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void SaveThenLoadRestoresSnapshotFields()
    {
        var store = new UsageCacheStore(_dir);
        var captured = DateTimeOffset.UtcNow.AddMinutes(-30);
        var reset = DateTimeOffset.UtcNow.AddHours(2);
        var snapshot = new UsageSnapshot
        {
            ToolName = "Claude Code",
            Plan = "Max 20x",
            CapturedAt = captured,
            Windows = new[]
            {
                new UsageWindow { Type = UsageWindowType.FiveHour, Label = "5h", UsedRatio = 0.42, ResetTime = reset },
                new UsageWindow { Type = UsageWindowType.Weekly, Label = "wk", UsedRatio = 0.8 },
            },
        };

        store.Save(new[] { snapshot });
        var loaded = Assert.Single(new UsageCacheStore(_dir).Load());

        Assert.Equal("Claude Code", loaded.ToolName);
        Assert.Equal("Max 20x", loaded.Plan);
        Assert.Equal(captured, loaded.CapturedAt);
        Assert.Equal(2, loaded.Windows.Count);

        var fiveHour = Assert.Single(loaded.Windows, w => w.Type == UsageWindowType.FiveHour);
        Assert.Equal(0.42, fiveHour.UsedRatio, 3);
        Assert.Equal(reset, fiveHour.ResetTime);
        // Labels are re-derived for the active language, never persisted-then-trusted.
        Assert.False(string.IsNullOrEmpty(fiveHour.Label));
    }

    [Fact]
    public void SaveThenLoadRestoresPerWindowStableId()
    {
        // Antigravity exposes two windows of the same Type; their stable Ids must survive the
        // round-trip so reconciliation and notification keys stay distinct after a restart.
        var snapshot = new UsageSnapshot
        {
            ToolName = "Antigravity",
            CapturedAt = DateTimeOffset.UtcNow,
            Windows = new[]
            {
                new UsageWindow { Id = "gemini-5h", Type = UsageWindowType.FiveHour, Label = "g5", UsedRatio = 0.1 },
                new UsageWindow { Id = "3p-5h", Type = UsageWindowType.FiveHour, Label = "c5", UsedRatio = 0.2 },
            },
        };

        new UsageCacheStore(_dir).Save(new[] { snapshot });
        var loaded = Assert.Single(new UsageCacheStore(_dir).Load());

        Assert.Equal(2, loaded.Windows.Count);
        Assert.Equal(new[] { "gemini-5h", "3p-5h" }, loaded.Windows.Select(w => w.Id));
        Assert.Equal(new[] { "gemini-5h", "3p-5h" }, loaded.Windows.Select(w => w.Key));
    }

    [Fact]
    public void LoadReturnsEmptyWhenFileAbsent()
    {
        Assert.Empty(new UsageCacheStore(_dir).Load());
    }

    [Fact]
    public void SaveOverwritesPreviousContents()
    {
        var store = new UsageCacheStore(_dir);
        store.Save(new[] { Snapshot("Claude Code"), Snapshot("Codex") });
        store.Save(new[] { Snapshot("Codex") });

        var loaded = new UsageCacheStore(_dir).Load();
        var single = Assert.Single(loaded);
        Assert.Equal("Codex", single.ToolName);
    }

    private static UsageSnapshot Snapshot(string name) => new()
    {
        ToolName = name,
        CapturedAt = DateTimeOffset.UtcNow,
        Windows = Array.Empty<UsageWindow>(),
    };

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
        catch { /* best effort */ }
    }
}
