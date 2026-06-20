using Gauge.Models;
using Gauge.ViewModels;

namespace Gauge.Tests;

public sealed class UsageViewModelTests
{
    [Fact]
    public void ApplyShowsOnlyToolsWithUsageWindows()
    {
        var viewModel = new UsageViewModel();

        viewModel.Apply(State(
            WithUsage("Claude Code", 0.42),
            WithoutUsage("Codex")));

        var card = Assert.Single(viewModel.Cards);
        Assert.Equal("Claude Code", card.ToolName);
        Assert.False(viewModel.IsEmpty);
        Assert.Equal("Claude Code 42%", viewModel.TrayTooltipSummary);
    }

    [Fact]
    public void ApplyRemovesCardWhenOnlyAnotherToolHasUsage()
    {
        var viewModel = new UsageViewModel();
        viewModel.Apply(State(WithUsage("Claude Code", 0.42), WithoutUsage("Codex")));

        viewModel.Apply(State(WithoutUsage("Claude Code"), WithUsage("Codex", 0.73)));

        var card = Assert.Single(viewModel.Cards);
        Assert.Equal("Codex", card.ToolName);
        Assert.Equal("Codex 73%", viewModel.TrayTooltipSummary);
    }

    [Fact]
    public void ApplyWithNoUsageWindowsShowsEmptyStateWithoutToolCards()
    {
        var viewModel = new UsageViewModel();

        viewModel.Apply(State(WithoutUsage("Claude Code"), WithoutUsage("Codex")));

        Assert.Empty(viewModel.Cards);
        Assert.True(viewModel.IsEmpty);
        Assert.Equal("Gauge", viewModel.TrayTooltipSummary);
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

    private static CachedUsage WithoutUsage(string toolName) => new()
    {
        ToolName = toolName,
        Snapshot = new UsageSnapshot
        {
            ToolName = toolName,
            CapturedAt = new DateTimeOffset(2026, 6, 20, 0, 0, 0, TimeSpan.Zero),
            Windows = Array.Empty<UsageWindow>(),
        },
    };
}
