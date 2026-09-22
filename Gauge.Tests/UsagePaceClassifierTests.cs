using Gauge.Models;
using Gauge.ViewModels;

namespace Gauge.Tests;

public sealed class UsagePaceClassifierTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 30, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(.60, .25, "−35% · 빠르게 소진 중")]
    [InlineData(.42, .25, "−17% · 빠르게 소진 중")]
    [InlineData(.32, .25, "−7% · 빠르게 소진 중")]
    [InlineData(.20, .45, "+25% · 여유 있음")]
    [InlineData(.25, .25, "0% · 적정")]
    [InlineData(.251, .25, "0% · 적정")]
    [InlineData(0, .25, "+25% · 여유 있음")]
    [InlineData(.99, .25, "−74% · 빠르게 소진 중")]
    // Exhausted windows drop the caption: "burning fast" beside 100% adds nothing.
    [InlineData(1, .25, "")]
    [InlineData(1.04, .25, "")]
    public void ShowsSignedDifferenceFromItsOwnCycle(double used, double elapsed, string expected)
    {
        Assert.Equal(expected, UsagePaceClassifier.ForRow(Window(used, elapsed), Now));
    }

    [Theory]
    [InlineData(5)]
    [InlineData(168)]
    [InlineData(720)]
    public void ExactlyThreePercentOpensGateForEveryDuration(int hours)
    {
        var duration = TimeSpan.FromHours(hours);
        var window = Window(.3, .03) with { Duration = duration, ResetTime = Now + duration * .97 };
        Assert.Equal("−27% · 빠르게 소진 중", UsagePaceClassifier.ForRow(window, Now));
        Assert.Empty(UsagePaceClassifier.ForRow(window with { ResetTime = window.ResetTime!.Value.AddTicks(1) }, Now));
        Assert.Empty(UsagePaceClassifier.ForRow(window with { ResetTime = Now + duration }, Now));
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
        }) Assert.Equal("–", UsagePaceClassifier.ForRow(invalid, Now));
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

    // ── Weekly pace models ──────────────────────────────────────────────────────
    // Monday 00:00 → next Monday 00:00 in UTC, so local weekdays are unambiguous.
    private static readonly DateTimeOffset Monday = new(2026, 8, 3, 0, 0, 0, TimeSpan.Zero);
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;

    [Fact]
    public void WorkDayModelExpectsNothingOverTheWeekend()
    {
        // Saturday noon, 60% used. Evenly spread, 5.5/7 of the week is gone and the row is
        // only +19% ahead; on a Monday-to-Friday week the whole quota was expected by
        // Friday night, so the same reading is +40% in reserve.
        var window = WeekWindow(.6);
        var saturdayNoon = Monday.AddDays(5.5);
        Assert.Equal("+19% · 여유 있음", UsagePaceClassifier.ForRow(window, saturdayNoon, WeeklyPaceModel.Uniform, zone: Utc));
        Assert.Equal("+40% · 여유 있음", UsagePaceClassifier.ForRow(window, saturdayNoon, WeeklyPaceModel.WorkDays, zone: Utc));
        // Sunday reads the same as Saturday: the expected curve is flat across the weekend.
        Assert.Equal("+40% · 여유 있음", UsagePaceClassifier.ForRow(window, Monday.AddDays(6.5), WeeklyPaceModel.WorkDays, zone: Utc));
    }

    [Fact]
    public void WorkDayModelSpendsTheWeekFasterOnWeekdays()
    {
        // Wednesday 00:00, 50% used: two of five work days (40%) versus two of seven (29%).
        var window = WeekWindow(.5);
        var wednesday = Monday.AddDays(2);
        Assert.Equal("−21% · 빠르게 소진 중", UsagePaceClassifier.ForRow(window, wednesday, WeeklyPaceModel.Uniform, zone: Utc));
        Assert.Equal("−10% · 빠르게 소진 중", UsagePaceClassifier.ForRow(window, wednesday, WeeklyPaceModel.WorkDays, zone: Utc));
    }

    [Fact]
    public void ShortWindowsIgnoreTheWeekdayModel()
    {
        // A 5-hour session on a Saturday afternoon: the work-day model would freeze its
        // expected curve at zero, so windows under two days always use the uniform spread.
        var duration = TimeSpan.FromHours(5);
        var start = Monday.AddDays(5).AddHours(13);
        var window = new UsageWindow
        {
            Type = UsageWindowType.FiveHour, UsedRatio = .5, Label = "5시간",
            Duration = duration, ResetTime = start + duration,
        };
        foreach (var model in Enum.GetValues<WeeklyPaceModel>())
            Assert.Equal("0% · 적정", UsagePaceClassifier.ForRow(window, start.AddHours(2.5), model, zone: Utc));
    }

    [Fact]
    public void EarlySilenceGateIsWallClockUnderEveryModel()
    {
        // Monday 01:00 is under 3% of the week however the days are weighted, and Saturday
        // 00:00 is past it even though the work-day curve has already reached 100%.
        var window = WeekWindow(.2);
        Assert.Empty(UsagePaceClassifier.ForRow(window, Monday.AddHours(1), WeeklyPaceModel.WorkDays, zone: Utc));
        Assert.Equal("+80% · 여유 있음", UsagePaceClassifier.ForRow(window, Monday.AddDays(5), WeeklyPaceModel.WorkDays, zone: Utc));
    }

    [Fact]
    public void AutomaticModelShapesTheCurveFromASufficientProfile()
    {
        // Monday noon, 25% used. A profile with three weeks of history that puts half the
        // week's consumption on Monday expects a quarter of the quota by Monday noon — on
        // pace — where the even spread expects 7% and calls it an 18% deficit.
        var window = WeekWindow(.25);
        var mondayNoon = Monday.AddHours(12);
        var profile = new UsageWeekdayProfile([0, 3, 1, 1, 1, 0, 0], DaysObserved: 21);
        Assert.Equal("0% · 적정", UsagePaceClassifier.ForRow(window, mondayNoon, WeeklyPaceModel.Automatic, profile, Utc));
        Assert.Equal("−18% · 빠르게 소진 중", UsagePaceClassifier.ForRow(window, mondayNoon, WeeklyPaceModel.Uniform, profile, Utc));
        // The profile is consulted by the automatic model only: the work-day model keeps its
        // own Monday-to-Friday weights (half a work day of five expects 10%).
        Assert.Equal("−15% · 빠르게 소진 중", UsagePaceClassifier.ForRow(window, mondayNoon, WeeklyPaceModel.WorkDays, profile, Utc));
    }

    [Fact]
    public void AutomaticModelReadsAsUniformWithoutEnoughHistory()
    {
        var window = WeekWindow(.25);
        var mondayNoon = Monday.AddHours(12);
        var uniform = UsagePaceClassifier.ForRow(window, mondayNoon, WeeklyPaceModel.Uniform, zone: Utc);
        Assert.Equal("−18% · 빠르게 소진 중", uniform);
        foreach (var insufficient in new UsageWeekdayProfile?[]
        {
            null,
            new([0, 3, 1, 1, 1, 0, 0], DaysObserved: UsageWeekdayProfile.MinimumDaysObserved - 1),
            new(new double[7], DaysObserved: 30),
            new([1, 1], DaysObserved: 30),
        }) Assert.Equal(uniform, UsagePaceClassifier.ForRow(window, mondayNoon, WeeklyPaceModel.Automatic, insufficient, Utc));
    }

    [Fact]
    public void CardPushesTheModelToEveryRowAndRowsRederiveInPlace()
    {
        // Rows keep their last window, so a model change from settings re-derives the caption
        // without waiting for a refresh; new rows inherit the card's model.
        var window = WeekWindow(.6) with { ResetTime = DateTimeOffset.UtcNow.AddDays(1.5) };
        var card = new ToolCardViewModel(new CachedUsage
        {
            ToolName = "Claude",
            Snapshot = new UsageSnapshot { ToolName = "Claude", Windows = [window] },
        });
        var row = Assert.Single(card.Windows);
        var before = row.PaceText;
        Assert.Equal(WeeklyPaceModel.Uniform, row.PaceModel);

        card.PaceModel = WeeklyPaceModel.WorkDays;
        Assert.Equal(WeeklyPaceModel.WorkDays, row.PaceModel);
        // Whether the caption moved depends on today's weekday; what must hold is that it
        // equals the classifier's answer under the new model.
        Assert.Equal(UsagePaceClassifier.ForRow(window, model: WeeklyPaceModel.WorkDays), row.PaceText);
        card.PaceModel = WeeklyPaceModel.Uniform;
        Assert.Equal(before, row.PaceText);

        card.PaceModel = WeeklyPaceModel.Automatic;
        card.Update(new CachedUsage
        {
            ToolName = "Claude",
            Snapshot = new UsageSnapshot { ToolName = "Claude", Windows = [window, window with { Id = "fable", GroupLabel = "Fable" }] },
        });
        Assert.All(card.Windows, r => Assert.Equal(WeeklyPaceModel.Automatic, r.PaceModel));
    }

    private static UsageWindow WeekWindow(double used) => new()
    {
        Type = UsageWindowType.Weekly, UsedRatio = used, Label = "주간",
        Duration = TimeSpan.FromDays(7), ResetTime = Monday.AddDays(7),
    };

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
