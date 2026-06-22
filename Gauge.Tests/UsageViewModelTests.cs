using Gauge.Localization;
using Gauge.Models;
using Gauge.Services;
using Gauge.ViewModels;

namespace Gauge.Tests;

public sealed class UsageViewModelTests
{
    [Fact]
    public void ApplyShowsCardForToolWithUsage()
    {
        var viewModel = new UsageViewModel();

        viewModel.Apply(State(
            WithUsage("Claude Code", 0.42),
            WithoutRecord("Codex")));

        var card = Assert.Single(viewModel.Cards);
        Assert.Equal("Claude Code", card.ToolName);
        Assert.False(viewModel.IsEmpty);
        Assert.Equal("Claude Code 42%", viewModel.TrayTooltipSummary);
    }

    [Fact]
    public void ApplyExcludesToolsWithNoRecord()
    {
        var viewModel = new UsageViewModel();

        viewModel.Apply(State(WithoutRecord("Claude Code"), WithoutRecord("Codex")));

        Assert.Empty(viewModel.Cards);
        Assert.True(viewModel.IsEmpty);
        Assert.Equal("AgentGauge", viewModel.TrayTooltipSummary);
    }

    [Fact]
    public void ApplyShowsNoDataCardForToolWithRecordButNoWindows()
    {
        var viewModel = new UsageViewModel();

        viewModel.Apply(State(WithEmptyRecord("Codex")));

        var card = Assert.Single(viewModel.Cards);
        Assert.Equal("Codex", card.ToolName);
        Assert.False(card.HasAnyData);
        Assert.Equal(Loc.Get("NoData"), card.StatusText);
        Assert.False(viewModel.IsEmpty);
        Assert.Equal(Loc.Format("Tray_NoData", "Codex"), viewModel.TrayTooltipSummary);
    }

    [Fact]
    public void ApplyShowsBothUsageAndNoDataCards()
    {
        var viewModel = new UsageViewModel();

        viewModel.Apply(State(
            WithUsage("Claude Code", 0.42),
            WithEmptyRecord("Codex")));

        Assert.Equal(2, viewModel.Cards.Count);
        Assert.False(viewModel.IsEmpty);
        Assert.Equal(
            $"Claude Code 42% · {Loc.Format("Tray_NoData", "Codex")}",
            viewModel.TrayTooltipSummary);
    }

    [Fact]
    public void ApplyRemovesCardWhenToolLosesItsRecord()
    {
        var viewModel = new UsageViewModel();
        viewModel.Apply(State(WithUsage("Claude Code", 0.42), WithoutRecord("Codex")));

        viewModel.Apply(State(WithoutRecord("Claude Code"), WithUsage("Codex", 0.73)));

        var card = Assert.Single(viewModel.Cards);
        Assert.Equal("Codex", card.ToolName);
        Assert.Equal("Codex 73%", viewModel.TrayTooltipSummary);
    }

    [Fact]
    public void ApplyOrdersCardsByRegistryDisplayOrder()
    {
        // Registry order is Codex before Claude Code; cards must follow it regardless of the
        // order the coordinator supplies the tools in.
        var registry = new ToolRegistry(new OrderedStore(ToolKind.Codex, ToolKind.ClaudeCode));
        var viewModel = new UsageViewModel(registry);

        viewModel.Apply(State(WithUsage("Claude Code", 0.42), WithUsage("Codex", 0.73)));

        Assert.Equal(new[] { "Codex", "Claude Code" }, viewModel.Cards.Select(c => c.ToolName));
    }

    [Fact]
    public void ReorderToolsReordersExistingCardsInPlace()
    {
        var registry = new ToolRegistry(new OrderedStore(ToolKind.ClaudeCode, ToolKind.Codex));
        var viewModel = new UsageViewModel(registry);
        viewModel.Apply(State(WithUsage("Claude Code", 0.42), WithUsage("Codex", 0.73)));

        // Dragging Codex above Claude on the main screen persists the new order…
        viewModel.ReorderTools(new[] { "Codex", "Claude Code" });
        // …and a subsequent coordinator push reflects it in the cards.
        viewModel.Apply(State(WithUsage("Claude Code", 0.42), WithUsage("Codex", 0.73)));

        Assert.Equal(new[] { "Codex", "Claude Code" }, viewModel.Cards.Select(c => c.ToolName));
        Assert.Equal(new[] { ToolKind.Codex, ToolKind.ClaudeCode }, registry.Enabled);
    }

    private sealed class OrderedStore(params ToolKind[] enabled) : IToolRegistryStore
    {
        private IReadOnlyCollection<ToolKind> _state = enabled;
        public IReadOnlyCollection<ToolKind> Load() => _state;
        public void Save(IReadOnlyCollection<ToolKind> e) => _state = e.ToList();
    }

    private static UsageState State(params CachedUsage[] tools) => new()
    {
        Tools = tools,
    };

    private static CachedUsage WithUsage(string toolName, double usedRatio)
    {
        var capturedAt = new DateTimeOffset(2026, 6, 20, 0, 0, 0, TimeSpan.Zero);
        return new CachedUsage
        {
            ToolName = toolName,
            LastUpdatedAt = capturedAt,
            Snapshot = new UsageSnapshot
            {
                ToolName = toolName,
                CapturedAt = capturedAt,
                Windows = new[]
                {
                    new UsageWindow
                    {
                        Type = UsageWindowType.FiveHour,
                        UsedRatio = usedRatio,
                        Label = "5-hour",
                    },
                },
            },
        };
    }

    // A tool Gauge has a record for (a snapshot) but with no usage windows — shown as a
    // "no data" card rather than excluded.
    private static CachedUsage WithEmptyRecord(string toolName) => new()
    {
        ToolName = toolName,
        Snapshot = new UsageSnapshot
        {
            ToolName = toolName,
            CapturedAt = new DateTimeOffset(2026, 6, 20, 0, 0, 0, TimeSpan.Zero),
            Windows = Array.Empty<UsageWindow>(),
        },
    };

    // A tool that has never succeeded (no snapshot: not signed in / no history) — left
    // off the usage surface entirely.
    private static CachedUsage WithoutRecord(string toolName) => new()
    {
        ToolName = toolName,
    };
}
