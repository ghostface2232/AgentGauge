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

    [Fact]
    public void ApplyShowsSignInCtaWhenNothingSignedInAndOnlyEmptyRecords()
    {
        // Fresh install: default tools "succeed" with empty snapshots (no credentials) and
        // the auth states are all Missing — the view must ask for sign-in, not show dead cards.
        var viewModel = new UsageViewModel(allToolsSignedOut: () => true);

        viewModel.Apply(State(WithEmptyRecord("Claude Code"), WithEmptyRecord("Codex")));

        Assert.True(viewModel.IsEmpty);
        Assert.Equal(Loc.Get("Empty_NotSignedIn"), viewModel.EmptyMessage);
        Assert.True(viewModel.IsSettingsCtaVisible);
    }

    [Fact]
    public void ApplyKeepsNoDataCardsWhenSomeToolIsSignedIn()
    {
        var viewModel = new UsageViewModel(allToolsSignedOut: () => false);

        viewModel.Apply(State(WithEmptyRecord("Claude Code")));

        Assert.False(viewModel.IsEmpty);
        Assert.False(viewModel.IsSettingsCtaVisible);
        Assert.Single(viewModel.Cards);
    }

    [Fact]
    public void ApplyShowsCtaWithFetchFailedMessageWhenAllToolsFailedWithoutCache()
    {
        var viewModel = new UsageViewModel();

        viewModel.Apply(State(
            WithFailedRecord("Claude Code"),
            WithFailedRecord("Codex")));

        Assert.True(viewModel.IsEmpty);
        Assert.Equal(Loc.Get("Empty_FetchFailed"), viewModel.EmptyMessage);
        Assert.True(viewModel.IsSettingsCtaVisible);
    }

    [Fact]
    public void ApplyHidesCtaWhileLoading()
    {
        var viewModel = new UsageViewModel(allToolsSignedOut: () => true);

        viewModel.Apply(UsageState.Empty);

        Assert.True(viewModel.IsEmpty);
        Assert.Equal(Loc.Get("Loading"), viewModel.EmptyMessage);
        Assert.False(viewModel.IsSettingsCtaVisible);
    }

    [Fact]
    public void ApplyClearsCtaOnceUsageArrives()
    {
        var signedOut = true;
        var viewModel = new UsageViewModel(allToolsSignedOut: () => signedOut);
        viewModel.Apply(State(WithEmptyRecord("Claude Code")));
        Assert.True(viewModel.IsSettingsCtaVisible);

        signedOut = false;
        viewModel.Apply(State(WithUsage("Claude Code", 0.42)));

        Assert.False(viewModel.IsEmpty);
        Assert.False(viewModel.IsSettingsCtaVisible);
        Assert.Single(viewModel.Cards);
    }

    private sealed class OrderedStore(params ToolKind[] enabled) : IToolRegistryStore
    {
        private IReadOnlyCollection<ToolKind> _state = enabled;
        public IReadOnlyCollection<ToolKind> Load() => _state;
        public void Save(IReadOnlyCollection<ToolKind> e) => _state = e.ToList();
    }

    [Fact]
    public void RefreshIndicatorFollowsStartAndCompleteNotApply()
    {
        var viewModel = new UsageViewModel();
        viewModel.Apply(State(WithUsage("Claude Code", 0.42), WithUsage("Codex", 0.10)));

        viewModel.SetRefreshing(new[] { "Codex", "Cursor" }); // Cursor has no card → ignored

        Assert.False(viewModel.Cards.Single(c => c.ToolName == "Claude Code").IsRefreshing);
        Assert.True(viewModel.Cards.Single(c => c.ToolName == "Codex").IsRefreshing);

        // A cached re-emit mid-fetch (debounce / gate bypass) must NOT hide the indicator.
        viewModel.Apply(State(WithUsage("Claude Code", 0.42), WithUsage("Codex", 0.10)));
        Assert.True(viewModel.Cards.Single(c => c.ToolName == "Codex").IsRefreshing);

        // Only the matching completion concludes it.
        viewModel.ClearRefreshing(new[] { "Codex" });
        Assert.All(viewModel.Cards, card => Assert.False(card.IsRefreshing));
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

    // A tool whose most recent refresh failed with nothing cached (network down / expired
    // login on a cold start).
    private static CachedUsage WithFailedRecord(string toolName) => new()
    {
        ToolName = toolName,
        LastRefreshFailed = true,
    };
}
