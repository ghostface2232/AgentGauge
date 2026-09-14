using Gauge.Models;
using Gauge.Services;
using Microsoft.Data.Sqlite;

namespace Gauge.Tests;

/// <summary>
/// A tool's display name is also its key in the usage history DB and the last-known cache,
/// so renaming "Claude Code" to "Claude" must carry both stores across the rename instead of
/// orphaning 90 days of history and one refresh of cached usage.
/// </summary>
public sealed class ToolRenameMigrationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "GaugeRenameTest_" + Guid.NewGuid().ToString("N"));
    private static readonly DateTimeOffset Now = new(2026, 9, 14, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void CatalogMapsTheOldClaudeNameToTheCurrentOne()
    {
        Assert.Equal("Claude", ToolCatalog.ClaudeCode.DisplayName);
        Assert.Equal("Claude", ToolCatalog.CurrentDisplayName("Claude Code"));
        Assert.Equal("Codex", ToolCatalog.CurrentDisplayName("Codex"));
    }

    [Fact]
    public void HistoryRecordedUnderTheOldNameIsReadableUnderTheNewOne()
    {
        using (var old = new UsageHistoryStore(_dir, new FixedTime(Now)))
        {
            old.Record(Snapshot("Claude Code", 0.4, Now.AddMinutes(-2)));
            old.Record(Snapshot("Claude Code", 0.5, Now.AddMinutes(-1)));
        }

        using var store = new UsageHistoryStore(_dir, new FixedTime(Now));
        var recent = store.GetRecent("Claude", UsageWindowType.FiveHour.ToString(), TimeSpan.FromHours(1));

        Assert.Equal(new[] { 0.4, 0.5 }, recent.Select(s => s.UsedRatio));
        Assert.Empty(store.GetRecent("Claude Code", UsageWindowType.FiveHour.ToString(), TimeSpan.FromHours(1)));
    }

    [Fact]
    public void CachedSnapshotUnderTheOldNameLoadsUnderTheNewOne()
    {
        var store = new UsageCacheStore(_dir);
        store.Save(new[] { Snapshot("Claude Code", 0.4, Now) });

        var loaded = Assert.Single(store.Load());

        Assert.Equal("Claude", loaded.ToolName);
    }

    private static UsageSnapshot Snapshot(string tool, double used, DateTimeOffset at) => new()
    {
        ToolName = tool,
        CapturedAt = at,
        Windows = [new UsageWindow { Type = UsageWindowType.FiveHour, Label = "5h", UsedRatio = used, ResetTime = at.AddHours(2) }],
    };

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
        catch { /* best effort */ }
    }
}
