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
        var card = new ToolCardViewModel(Cached("Claude",
            Window(null, null, UsageWindowType.FiveHour)));

        // Default is the bar layout.
        Assert.True(card.IsBarMode);
        Assert.False(card.IsGaugeMode);

        card.ViewMode = UsageViewMode.Gauge;

        Assert.False(card.IsBarMode);
        Assert.True(card.IsGaugeMode);
    }

    [Fact]
    public void DisplayBasisReachesExistingAndLaterAddedRows()
    {
        var card = new ToolCardViewModel(Cached("Claude",
            Window("5h", null, UsageWindowType.FiveHour)));
        Assert.Equal("10%", card.Windows.Single().PercentText);

        card.DisplayBasis = UsageDisplayBasis.Remaining;

        Assert.Equal("90%", card.Windows.Single().PercentText);

        // A window that appears on a later refresh inherits the card's basis.
        card.Update(Cached("Claude",
            Window("5h", null, UsageWindowType.FiveHour),
            Window("weekly", null, UsageWindowType.Weekly)));

        Assert.All(card.Windows, row => Assert.Equal(UsageDisplayBasis.Remaining, row.DisplayBasis));
        Assert.Equal(new[] { "90%", "90%" }, card.Windows.Select(r => r.PercentText));
        // The gauge layout shares the same row instances, so it reflects the flip with no
        // separate push.
        Assert.Equal(new[] { "90%", "90%" }, card.GaugeGroups.SelectMany(g => g.Rows).Select(r => r.PercentText));
    }

    [Fact]
    public void SparklinePreferenceReachesExistingAndLaterAddedRows()
    {
        var card = new ToolCardViewModel(Cached("Claude",
            Window("5h", null, UsageWindowType.FiveHour)));
        Assert.True(card.ShowSparkline);
        Assert.True(card.Windows.Single().ShowSparkline);

        card.ShowSparkline = false;

        Assert.False(card.Windows.Single().ShowSparkline);

        // A window that appears on a later refresh inherits the card's preference.
        card.Update(Cached("Claude",
            Window("5h", null, UsageWindowType.FiveHour),
            Window("weekly", null, UsageWindowType.Weekly)));

        Assert.All(card.Windows, row => Assert.False(row.ShowSparkline));
    }

    [Fact]
    public void RefreshIssueDotOnlyTracksFailedLastAttempt()
    {
        var healthy = Cached("Claude", Window(null, null, UsageWindowType.FiveHour));
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

        Assert.Equal(new[] { "gemini-5h", "3p-5h" }, card.GaugeGroups.Select(g => g.Key));
        // The family change lands on a row boundary, so the divider sits above the second row.
        Assert.Equal(new[] { false, true }, card.GaugeGroups.Select(g => g.ShowDivider));
        // Each family's gauges are ordered 5h-then-weekly, same as the bar layout.
        Assert.Equal(new[] { "gemini-5h", "gemini-weekly" }, card.GaugeGroups[0].Rows.Select(r => r.Key));
        Assert.Equal(new[] { "3p-5h", "3p-weekly" }, card.GaugeGroups[1].Rows.Select(r => r.Key));
    }

    [Fact]
    public void GaugeGroupsCollapseUngroupedToolIntoOneRowWithoutDivider()
    {
        var card = new ToolCardViewModel(Cached("Claude",
            Window(null, null, UsageWindowType.FiveHour),
            Window(null, null, UsageWindowType.Weekly)));

        var group = Assert.Single(card.GaugeGroups);
        Assert.False(group.ShowDivider);
        Assert.Equal(new[] { "FiveHour", "Weekly" }, group.Rows.Select(r => r.Key));
    }

    [Fact]
    public void LeavesUngroupedToolOrderUntouchedAndHeaderless()
    {
        var card = new ToolCardViewModel(Cached("Claude",
            Window(null, null, UsageWindowType.FiveHour),
            Window(null, null, UsageWindowType.Weekly)));

        Assert.Equal(new[] { "FiveHour", "Weekly" }, card.Windows.Select(r => r.Key));
        Assert.All(card.Windows, r => Assert.Empty(r.GroupHeader));
    }

    [Fact]
    public void ScopedWindowAfterAccountWindowsGetsHeadingAndDivider()
    {
        var card = new ToolCardViewModel(Cached("Claude",
            Window(null, null, UsageWindowType.FiveHour),
            Window(null, null, UsageWindowType.Weekly),
            Window("claude-weekly-scoped-fable", "Fable", UsageWindowType.Weekly)));

        Assert.Equal(new[] { "FiveHour", "Weekly", "claude-weekly-scoped-fable" },
            card.Windows.Select(r => r.Key));
        Assert.Equal("Fable", card.Windows[2].GroupHeader);
        Assert.True(card.Windows[2].ShowGroupDivider);

        Assert.Equal(new[] { "FiveHour", "claude-weekly-scoped-fable" }, card.GaugeGroups.Select(g => g.Key));
        Assert.False(card.GaugeGroups[0].ShowDivider);
        Assert.True(card.GaugeGroups[1].ShowDivider);
    }

    [Fact]
    public void GaugeRowsFillEmptySlotsAcrossFamilies()
    {
        var card = new ToolCardViewModel(Cached("Claude",
            Window(null, null, UsageWindowType.Weekly),
            Window("claude-weekly-scoped-fable", "Fable", UsageWindowType.Weekly)));

        var row = Assert.Single(card.GaugeGroups);
        Assert.False(row.ShowDivider);
        Assert.Equal(new[] { "Weekly", "claude-weekly-scoped-fable" }, row.Rows.Select(r => r.Key));
        // The ungrouped gauge keeps a blank family line so both rings sit at the same height.
        Assert.True(row.Rows[0].ShowFamilyLine);
        Assert.False(row.Rows[0].HasFamilyLabel);
    }

    [Theory]
    [InlineData(-1, false)]
    [InlineData(12, true)]
    [InlineData(36, true)]
    [InlineData(49, false)]
    public void ResetCreditChipOnlyShowsAnUpcomingDeadline(int hours, bool visible)
    {
        var card = new ToolCardViewModel(CachedWithDeadline(hours));

        Assert.Equal(visible, card.HasResetCreditWarning);
        Assert.Equal(visible, card.ResetCreditsDescription.Contains("만료"));
        if (visible) Assert.Contains("초기화권", card.WarningResetCreditsText);
    }

    [Fact]
    public void HeaderChipsDropTheirLabelsOnlyWhenTheApiCostAndBothChipsAreShown()
    {
        var card = new ToolCardViewModel(CachedWithDeadline(36));
        var incident = new ProviderStatus(ToolKind.Codex, ProviderStatusLevel.Major, "Incident", DateTimeOffset.UtcNow);
        var cost = new ApiCostEstimate("Codex", 2026, 10, 12m, new TokenTotals(1, 0, 0, 0, 1), 0, [], DateTimeOffset.UtcNow);
        var changed = new List<string?>();
        card.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        // Any two of the three still fit.
        card.ApplyServiceStatus(incident);
        Assert.True(card.ShowHeaderChipLabels);

        card.ApplyApiCost(cost);
        Assert.False(card.ShowHeaderChipLabels);
        Assert.Equal("2일", card.WarningResetCreditsText);
        Assert.Contains(nameof(ToolCardViewModel.ShowHeaderChipLabels), changed);

        // Losing any one of them brings the labels back.
        card.Update(CachedWithDeadline(null));
        Assert.True(card.ShowHeaderChipLabels);
        card.Update(CachedWithDeadline(36));
        Assert.False(card.ShowHeaderChipLabels);
        card.ApplyApiCost(null);
        Assert.True(card.ShowHeaderChipLabels);
        card.ApplyApiCost(cost);
        card.ApplyServiceStatus(incident with { Level = ProviderStatusLevel.Operational });
        Assert.True(card.ShowHeaderChipLabels);
    }

    private static UsageWindow Window(string? id, string? group, UsageWindowType type) => new()
    {
        Id = id,
        GroupLabel = group,
        Type = type,
        Label = type.ToString(),
        UsedRatio = 0.1,
    };

    private static CachedUsage CachedWithDeadline(int? hours)
        => Cached("Codex", hours is { } value ? [new(DateTimeOffset.UtcNow.AddHours(value))] : null,
            Window(null, null, UsageWindowType.FiveHour));

    private static CachedUsage Cached(string toolName, params UsageWindow[] windows)
        => Cached(toolName, null, windows);

    private static CachedUsage Cached(string toolName, ResetCreditDeadline[]? deadlines, params UsageWindow[] windows) => new()
    {
        ToolName = toolName,
        Snapshot = new UsageSnapshot
        {
            ToolName = toolName,
            CapturedAt = DateTimeOffset.UtcNow,
            Windows = windows,
            ResetCreditDeadlines = deadlines,
        },
        LastUpdatedAt = DateTimeOffset.UtcNow,
    };
}
