using Gauge.Models;
using Gauge.Services;

namespace Gauge.Tests;

public sealed class UsageNotificationEvaluatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 19, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void FirstObservationAboveThreshold_DoesNotNotify()
    {
        var evaluator = new UsageNotificationEvaluator();

        var result = evaluator.Evaluate(State(UsageWindowType.Weekly, 0.95, Now.AddDays(4), Now), Now);

        Assert.Empty(result);
    }

    [Fact]
    public void Weekly_CrossesSeventyAndNinetyOnlyOncePerCycle()
    {
        var evaluator = new UsageNotificationEvaluator();
        evaluator.Evaluate(State(UsageWindowType.Weekly, 0.60, Now.AddDays(4), Now), Now);

        var caution = evaluator.Evaluate(State(UsageWindowType.Weekly, 0.72, Now.AddDays(4), Now.AddMinutes(1)), Now);
        var repeat = evaluator.Evaluate(State(UsageWindowType.Weekly, 0.80, Now.AddDays(4), Now.AddMinutes(2)), Now);
        var danger = evaluator.Evaluate(State(UsageWindowType.Weekly, 0.91, Now.AddDays(4), Now.AddMinutes(3)), Now);

        Assert.Single(caution);
        Assert.Equal(UsageLevel.Caution, caution[0].Level);
        Assert.Empty(repeat);
        Assert.Single(danger);
        Assert.Equal(UsageLevel.Danger, danger[0].Level);
    }

    [Fact]
    public void FiveHour_OnlyNotifiesAtNinety()
    {
        var evaluator = new UsageNotificationEvaluator();
        evaluator.Evaluate(State(UsageWindowType.FiveHour, 0.60, Now.AddHours(3), Now), Now);

        var seventy = evaluator.Evaluate(State(UsageWindowType.FiveHour, 0.75, Now.AddHours(3), Now.AddMinutes(1)), Now);
        var ninety = evaluator.Evaluate(State(UsageWindowType.FiveHour, 0.90, Now.AddHours(3), Now.AddMinutes(2)), Now);

        Assert.Empty(seventy);
        Assert.Single(ninety);
    }

    [Fact]
    public void AdvancedResetTimeAndUsageDrop_AfterAlert_NotifiesReset()
    {
        var evaluator = new UsageNotificationEvaluator();
        evaluator.Evaluate(State(UsageWindowType.FiveHour, 0.80, Now.AddHours(1), Now), Now);
        evaluator.Evaluate(State(UsageWindowType.FiveHour, 0.95, Now.AddHours(1), Now.AddMinutes(1)), Now);

        var reset = evaluator.Evaluate(
            State(UsageWindowType.FiveHour, 0.02, Now.AddHours(6), Now.AddHours(1)),
            Now.AddHours(1));

        Assert.Single(reset);
        Assert.Equal(UsageNotificationKind.Reset, reset[0].Kind);
        Assert.Equal("현재 98%로 한도 초기화됨", reset[0].Message);
    }

    [Fact]
    public void FailedRefreshWithCachedSnapshot_DoesNotCreateTransition()
    {
        var evaluator = new UsageNotificationEvaluator();
        evaluator.Evaluate(State(UsageWindowType.Weekly, 0.60, Now.AddDays(2), Now), Now);

        var result = evaluator.Evaluate(
            State(UsageWindowType.Weekly, 0.95, Now.AddDays(2), Now.AddMinutes(1), failed: true),
            Now.AddMinutes(1));
        var recovered = evaluator.Evaluate(
            State(UsageWindowType.Weekly, 0.72, Now.AddDays(2), Now.AddMinutes(2)),
            Now.AddMinutes(2));

        Assert.Empty(result);
        Assert.Single(recovered);
        Assert.Equal(UsageLevel.Caution, recovered[0].Level);
    }

    private static UsageState State(
        UsageWindowType type,
        double ratio,
        DateTimeOffset reset,
        DateTimeOffset captured,
        bool failed = false)
    {
        var snapshot = new UsageSnapshot
        {
            ToolName = "Codex",
            CapturedAt = captured,
            Windows =
            [
                new UsageWindow
                {
                    Type = type,
                    UsedRatio = ratio,
                    Label = type == UsageWindowType.FiveHour ? "5시간" : "주간",
                    ResetTime = reset,
                },
            ],
        };
        return new UsageState
        {
            LastUpdatedAt = captured,
            Tools =
            [
                new CachedUsage
                {
                    ToolName = snapshot.ToolName,
                    Snapshot = snapshot,
                    LastUpdatedAt = captured,
                    LastRefreshFailed = failed,
                },
            ],
        };
    }
}
