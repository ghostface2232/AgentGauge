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
        // The divider sits only between families — above Claude/GPT, never above the first group.
        Assert.Equal(
            new[] { false, false, true, false },
            card.Windows.Select(r => r.ShowGroupDivider));
    }

    [Fact]
    public void ViewModeDrivesBarAndGaugeFlags()
    {
        var card = new ToolCardViewModel(Cached("Claude Code",
            Window(null, null, UsageWindowType.FiveHour)));

        // Default is the bar layout.
        Assert.True(card.IsBarMode);
        Assert.False(card.IsGaugeMode);

        card.ViewMode = UsageViewMode.Gauge;

        Assert.False(card.IsBarMode);
        Assert.True(card.IsGaugeMode);
    }

    [Fact]
    public void RefreshIssueDotOnlyTracksFailedLastAttempt()
    {
        var healthy = Cached("Claude Code", Window(null, null, UsageWindowType.FiveHour));
        var card = new ToolCardViewModel(healthy);

        Assert.False(card.HasRefreshIssue);

        card.Update(healthy with { LastRefreshFailed = true });
        Assert.True(card.HasRefreshIssue);

        card.Update(healthy);
        Assert.False(card.HasRefreshIssue);
    }

    [Fact]
    public void GaugeRowsCarryFamilyLabelOnEveryWindow()
    {
        // Unlike the bar layout's first-row-only GroupHeader, each gauge is self-labeled
        // with its family so it stands alone in a grid cell.
        var card = new ToolCardViewModel(Cached("Antigravity",
            Window("gemini-5h", "Gemini", UsageWindowType.FiveHour),
            Window("gemini-weekly", "Gemini", UsageWindowType.Weekly)));

        Assert.Equal(new[] { "Gemini", "Gemini" }, card.Windows.Select(r => r.FamilyLabel));
        Assert.All(card.Windows, r => Assert.True(r.HasFamilyLabel));
    }

    [Fact]
    public void GaugeGroupsSplitFamiliesWithDividerBetweenThem()
    {
        var card = new ToolCardViewModel(Cached("Antigravity",
            Window("gemini-weekly", "Gemini", UsageWindowType.Weekly),
            Window("gemini-5h", "Gemini", UsageWindowType.FiveHour),
            Window("3p-weekly", "Claude/GPT", UsageWindowType.Weekly),
            Window("3p-5h", "Claude/GPT", UsageWindowType.FiveHour)));

        Assert.Equal(new[] { "Gemini", "Claude/GPT" }, card.GaugeGroups.Select(g => g.Key));
        // Divider sits above the second family only.
        Assert.Equal(new[] { false, true }, card.GaugeGroups.Select(g => g.ShowDivider));
        // Each family's gauges are ordered 5h-then-weekly, same as the bar layout.
        Assert.Equal(new[] { "gemini-5h", "gemini-weekly" }, card.GaugeGroups[0].Rows.Select(r => r.Key));
        Assert.Equal(new[] { "3p-5h", "3p-weekly" }, card.GaugeGroups[1].Rows.Select(r => r.Key));
    }

    [Fact]
    public void GaugeGroupsCollapseUngroupedToolIntoOneRowWithoutDivider()
    {
        var card = new ToolCardViewModel(Cached("Claude Code",
            Window(null, null, UsageWindowType.FiveHour),
            Window(null, null, UsageWindowType.Weekly)));

        var group = Assert.Single(card.GaugeGroups);
        Assert.False(group.ShowDivider);
        Assert.Equal(new[] { "FiveHour", "Weekly" }, group.Rows.Select(r => r.Key));
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

    [Fact]
    public void ScopedWindowAfterAccountWindowsGetsHeadingAndDivider()
    {
        var card = new ToolCardViewModel(Cached("Claude Code",
            Window(null, null, UsageWindowType.FiveHour),
            Window(null, null, UsageWindowType.Weekly),
            Window("claude-weekly-scoped-fable", "Fable", UsageWindowType.Weekly)));

        Assert.Equal(new[] { "FiveHour", "Weekly", "claude-weekly-scoped-fable" },
            card.Windows.Select(r => r.Key));
        Assert.Equal("Fable", card.Windows[2].GroupHeader);
        Assert.True(card.Windows[2].ShowGroupDivider);

        Assert.Equal(new[] { "", "Fable" }, card.GaugeGroups.Select(g => g.Key));
        Assert.False(card.GaugeGroups[0].ShowDivider);
        Assert.True(card.GaugeGroups[1].ShowDivider);
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
