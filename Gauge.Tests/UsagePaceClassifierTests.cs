using Gauge.Models;
using Gauge.ViewModels;

namespace Gauge.Tests;

public sealed class UsagePaceClassifierTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 30, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void AheadOfEvenPaceShowsCompactWarning()
    {
        var window = Window(used: 0.60, elapsed: 0.25);

        var (text, level) = UsagePaceClassifier.ForRow(window, Now);

        Assert.Equal("빠르게 소진 중", text);
        Assert.Equal(UsageLevel.Danger, level);
    }

    [Fact]
    public void SmallLeadOrMissingDurationStaysQuiet()
    {
        Assert.Empty(UsagePaceClassifier.ForRow(Window(used: 0.32, elapsed: 0.25), Now).Text);

        var withoutDuration = Window(used: 0.80, elapsed: 0.25) with { Duration = null };
        Assert.Empty(UsagePaceClassifier.ForRow(withoutDuration, Now).Text);
    }

    [Fact]
    public void ModerateLeadUsesCautionLevel()
    {
        var (text, level) = UsagePaceClassifier.ForRow(Window(used: 0.42, elapsed: 0.25), Now);
        Assert.Equal("빠르게 소진 중", text);
        Assert.Equal(UsageLevel.Caution, level);
    }

    [Fact]
    public void VeryEarlyCycleStaysQuiet()
    {
        var fiveHour = Window(used: 0.30, elapsed: 0.02) with
        {
            Type = UsageWindowType.FiveHour,
            Duration = TimeSpan.FromHours(5),
            ResetTime = Now.AddHours(4.9),
        };

        Assert.Empty(UsagePaceClassifier.ForRow(fiveHour, Now).Text);
    }

    private static UsageWindow Window(double used, double elapsed)
    {
        var duration = TimeSpan.FromDays(7);
        var reset = Now + TimeSpan.FromTicks((long)(duration.Ticks * (1 - elapsed)));
        return new UsageWindow
        {
            Type = UsageWindowType.Weekly,
            UsedRatio = used,
            Label = "주간",
            ResetTime = reset,
            Duration = duration,
        };
    }
}
