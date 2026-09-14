using Gauge.Models;
using Gauge.ViewModels;

namespace Gauge.Tests;

public sealed class UsagePaceClassifierTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 30, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(.60, .25, "−35% · 빠르게 소진 중", UsageLevel.Danger)]
    [InlineData(.42, .25, "−17% · 빠르게 소진 중", UsageLevel.Caution)]
    [InlineData(.32, .25, "−7% · 빠르게 소진 중", UsageLevel.Ok)]
    [InlineData(.20, .45, "+25% · 여유 있음", UsageLevel.Ok)]
    [InlineData(.25, .25, "0% · 적정", UsageLevel.Ok)]
    [InlineData(.251, .25, "0% · 적정", UsageLevel.Ok)]
    [InlineData(0, .25, "+25% · 여유 있음", UsageLevel.Ok)]
    [InlineData(.99, .25, "−74% · 빠르게 소진 중", UsageLevel.Danger)]
    // Exhausted windows drop the caption: "burning fast" beside 100% adds nothing.
    [InlineData(1, .25, "", UsageLevel.Ok)]
    [InlineData(1.04, .25, "", UsageLevel.Ok)]
    public void ShowsSignedDifferenceFromItsOwnCycle(double used, double elapsed, string expected, UsageLevel level)
    {
        Assert.Equal((expected, level), UsagePaceClassifier.ForRow(Window(used, elapsed), Now));
    }

    [Theory]
    [InlineData(5)]
    [InlineData(168)]
    [InlineData(720)]
    public void ExactlyThreePercentOpensGateForEveryDuration(int hours)
    {
        var duration = TimeSpan.FromHours(hours);
        var window = Window(.3, .03) with { Duration = duration, ResetTime = Now + duration * .97 };
        Assert.Equal("−27% · 빠르게 소진 중", UsagePaceClassifier.ForRow(window, Now).Text);
        Assert.Empty(UsagePaceClassifier.ForRow(window with { ResetTime = window.ResetTime!.Value.AddTicks(1) }, Now).Text);
        Assert.Empty(UsagePaceClassifier.ForRow(window with { ResetTime = Now + duration }, Now).Text);
    }

    [Fact]
    public void MissingOrInvalidCycleShowsEnDash()
    {
        var window = Window(.8, .25);
        foreach (var invalid in new[]
        {
            window with { Duration = null }, window with { Duration = TimeSpan.Zero },
            window with { Duration = TimeSpan.FromHours(-1) }, window with { ResetTime = null },
            window with { ResetTime = Now }, window with { ResetTime = Now.AddDays(-1) },
            window with { ResetTime = Now.AddDays(8) }, window with { UsedRatio = double.NaN },
            window with { UsedRatio = double.PositiveInfinity },
        }) Assert.Equal(("–", UsageLevel.Ok), UsagePaceClassifier.ForRow(invalid, Now));
    }

    [Fact]
    public void AnotherWindowCannotSupplyMissingPaceData()
    {
        var card = new ToolCardViewModel(new CachedUsage
        {
            ToolName = "Codex",
            Snapshot = new UsageSnapshot
            {
                ToolName = "Codex", Windows =
                [Window(.6, .25) with { Id = "weekly", ResetTime = DateTimeOffset.UtcNow.AddDays(5.25) },
                 Window(.3, .25) with { Id = "scoped", GroupLabel = "Model", Duration = null }],
            },
        });
        Assert.Equal("–", Assert.Single(card.Windows, w => w.Key == "scoped").PaceText);
        Assert.True(Assert.Single(card.Windows, w => w.Key == "scoped").HasPace);
        Assert.Contains("빠르게 소진 중", Assert.Single(card.Windows, w => w.Key == "weekly").PaceText);
    }

    private static UsageWindow Window(double used, double elapsed)
    {
        var duration = TimeSpan.FromDays(7);
        return new UsageWindow
        {
            Type = UsageWindowType.Weekly, UsedRatio = used, Label = "주간",
            ResetTime = Now + duration * (1 - elapsed), Duration = duration,
        };
    }
}
