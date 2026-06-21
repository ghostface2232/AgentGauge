using Gauge.Models;
using Gauge.ViewModels;

namespace Gauge.Tests;

/// <summary>
/// Card-level grouping of windows by model family (Antigravity), and that ungrouped tools are
/// left exactly as the provider ordered them.
/// </summary>
public sealed class ToolCardViewModelTests
{
    [Fact]
    public void GroupsWindowsByFamilyWithFiveHourFirstAndHeaderOnFirstRow()
    {
        // Provider order is weekly-then-5h within each family; display should be 5h-then-weekly,
        // families contiguous, with the family heading only on each group's first row.
        var card = new ToolCardViewModel(Cached("Antigravity",
            Window("gemini-weekly", "Gemini", UsageWindowType.Weekly),
            Window("gemini-5h", "Gemini", UsageWindowType.FiveHour),
            Window("3p-weekly", "Claude/GPT", UsageWindowType.Weekly),
            Window("3p-5h", "Claude/GPT", UsageWindowType.FiveHour)));

        Assert.Equal(
            new[] { "gemini-5h", "gemini-weekly", "3p-5h", "3p-weekly" },
            card.Windows.Select(r => r.Key));
        Assert.Equal(
            new[] { "Gemini", "", "Claude/GPT", "" },
            card.Windows.Select(r => r.GroupHeader));
        Assert.Equal(
            new[] { true, false, true, false },
            card.Windows.Select(r => r.HasGroupHeader));
    }

    [Fact]
    public void LeavesUngroupedToolOrderUntouchedAndHeaderless()
    {
        var card = new ToolCardViewModel(Cached("Claude Code",
            Window(null, null, UsageWindowType.FiveHour),
            Window(null, null, UsageWindowType.Weekly)));

        Assert.Equal(new[] { "FiveHour", "Weekly" }, card.Windows.Select(r => r.Key));
        Assert.All(card.Windows, r => Assert.Empty(r.GroupHeader));
    }

    private static UsageWindow Window(string? id, string? group, UsageWindowType type) => new()
    {
        Id = id,
        GroupLabel = group,
        Type = type,
        Label = type.ToString(),
        UsedRatio = 0.1,
    };

    private static CachedUsage Cached(string toolName, params UsageWindow[] windows) => new()
    {
        ToolName = toolName,
        Snapshot = new UsageSnapshot { ToolName = toolName, CapturedAt = DateTimeOffset.UtcNow, Windows = windows },
        LastUpdatedAt = DateTimeOffset.UtcNow,
    };
}
