using Gauge.Models;
using Gauge.Services;

namespace Gauge.Tests;

public sealed class UsageNotificationEvaluatorTests
{
    [Fact]
    public void NewLoginInTheSameProfileStartsAFreshAlertBaseline()
    {
        var evaluator = new UsageNotificationEvaluator();
        var reset = Now.AddDays(4);
        evaluator.Evaluate(State(UsageWindowType.Weekly, .2, reset, Now, identity: "alice"), Now);
        var switched = State(UsageWindowType.Weekly, .8, reset, Now.AddMinutes(1), identity: "bob");
        Assert.Empty(evaluator.Evaluate(switched, Now));
        var crossing = State(UsageWindowType.Weekly, .95, reset, Now.AddMinutes(2), identity: "bob");
        Assert.Single(evaluator.Evaluate(crossing, Now));
    }

    [Fact]
    public void ResetCreditExpiryDeduplicatesByLoginAndNamesTheProfile()
    {
        var evaluator = new UsageNotificationEvaluator(_ => "Codex · Work");
        var expiry = Now.AddDays(2);
        var alice = State(UsageWindowType.Weekly, .2, null, Now, identity: "alice", creditExpiry: expiry);
        var bob = State(UsageWindowType.Weekly, .2, null, Now, identity: "bob", creditExpiry: expiry);

        Assert.Equal(UsageNotificationKind.ResetCredits, Assert.Single(evaluator.Evaluate(alice, Now)).Kind);
        var alert = Assert.Single(evaluator.Evaluate(bob, Now));
        Assert.Contains("Codex · Work", alert.Title);
        Assert.Empty(evaluator.Evaluate(bob, Now));
    }

    private static readonly DateTimeOffset Now = new(2026, 6, 19, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HideShowSkipsHiddenCrossingsIncludingCacheAndFailure(bool reset)
    {
        var evaluator = new UsageNotificationEvaluator();
        var initial = State(UsageWindowType.Weekly, .6, Now.AddDays(4), Now);
        evaluator.Evaluate(initial, Now);
        evaluator.SetToolHidden("Codex", true);
        Assert.Empty(evaluator.Evaluate(initial, Now));
        evaluator.SetToolHidden("Codex", false);
        Assert.Empty(evaluator.Evaluate(initial, Now));
        var end = reset ? Now.AddDays(11) : Now.AddDays(4);
        Assert.Empty(evaluator.Evaluate(State(UsageWindowType.Weekly, .8, end, Now.AddMinutes(1), failed: true), Now));
        Assert.Empty(evaluator.Evaluate(State(UsageWindowType.Weekly, .8, end, Now.AddMinutes(2)), Now));
        Assert.Single(evaluator.Evaluate(State(UsageWindowType.Weekly, .95, end, Now.AddMinutes(3)), Now));
    }

    [Fact]
    public void SnapshotCompletedWhileHidden_KeepsLatestTimestampForResumeBaseline()
    {
        // 60%/t0 -> hide -> in-flight request completes 65%/t1 while hidden -> show.
        // The cache re-serve of 65%/t1 must not consume the resume baseline, so the
        // first live poll (80%/t2) becomes the baseline instead of replaying 70%.
        var evaluator = new UsageNotificationEvaluator();
        evaluator.Evaluate(State(UsageWindowType.Weekly, .60, Now.AddDays(4), Now), Now);
        evaluator.SetToolHidden("Codex", true);
        Assert.Empty(evaluator.Evaluate(State(UsageWindowType.Weekly, .65, Now.AddDays(4), Now.AddMinutes(1)), Now));
        evaluator.SetToolHidden("Codex", false);

        var cachedReserve = evaluator.Evaluate(State(UsageWindowType.Weekly, .65, Now.AddDays(4), Now.AddMinutes(1)), Now);
        var firstLive = evaluator.Evaluate(State(UsageWindowType.Weekly, .80, Now.AddDays(4), Now.AddMinutes(2)), Now);
        var nextCrossing = evaluator.Evaluate(State(UsageWindowType.Weekly, .95, Now.AddDays(4), Now.AddMinutes(3)), Now);

        Assert.Empty(cachedReserve);
        Assert.Empty(firstLive);
        Assert.Single(nextCrossing);
        Assert.Equal(UsageLevel.Danger, nextCrossing[0].Level);
    }

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
    public void SameTypeWindows_TrackThresholdsIndependentlyById()
    {
        // A tool with two 5-hour windows (e.g. Antigravity's Gemini and Claude/GPT limits).
        // Keying by window Id, not Type, keeps their baselines and threshold masks separate.
        var evaluator = new UsageNotificationEvaluator();
        evaluator.Evaluate(TwoFiveHour(0.60, 0.60, Now), Now);

        // Only the first window crosses 90%.
        var first = evaluator.Evaluate(TwoFiveHour(0.92, 0.61, Now.AddMinutes(1)), Now.AddMinutes(1));
        Assert.Single(first);
        Assert.Equal(UsageLevel.Danger, first[0].Level);

        // The second window crossing later still fires — it was not masked by the first.
        var second = evaluator.Evaluate(TwoFiveHour(0.93, 0.94, Now.AddMinutes(2)), Now.AddMinutes(2));
        Assert.Single(second);
        Assert.Equal(UsageLevel.Danger, second[0].Level);
    }

    [Fact]
    public void ScopedWindow_IdentifiesGroupInThresholdAndResetTitles()
    {
        var evaluator = new UsageNotificationEvaluator();
        evaluator.Evaluate(
            State(UsageWindowType.Weekly, 0.60, Now.AddDays(4), Now,
                id: "claude-weekly-scoped-fable", groupLabel: "Fable"),
            Now);

        var threshold = evaluator.Evaluate(
            State(UsageWindowType.Weekly, 0.72, Now.AddDays(4), Now.AddMinutes(1),
                id: "claude-weekly-scoped-fable", groupLabel: "Fable"),
            Now.AddMinutes(1));
        var reset = evaluator.Evaluate(
            State(UsageWindowType.Weekly, 0.05, Now.AddDays(11), Now.AddMinutes(2),
                id: "claude-weekly-scoped-fable", groupLabel: "Fable"),
            Now.AddMinutes(2));

        Assert.Single(threshold);
        Assert.Contains("Fable", threshold[0].Title);
        Assert.Single(reset);
        Assert.Contains("Fable", reset[0].Title);
    }

    [Fact]
    public void BillingCycle_CrossesSeventyAndNinetyOnlyOncePerCycle()
    {
        var evaluator = new UsageNotificationEvaluator();
        evaluator.Evaluate(State(UsageWindowType.BillingCycle, 0.60, Now.AddDays(20), Now), Now);

        var caution = evaluator.Evaluate(State(UsageWindowType.BillingCycle, 0.72, Now.AddDays(20), Now.AddMinutes(1)), Now);
        var repeat = evaluator.Evaluate(State(UsageWindowType.BillingCycle, 0.80, Now.AddDays(20), Now.AddMinutes(2)), Now);
        var danger = evaluator.Evaluate(State(UsageWindowType.BillingCycle, 0.91, Now.AddDays(20), Now.AddMinutes(3)), Now);

        Assert.Single(caution);
        Assert.Equal<UsageWindowType?>(UsageWindowType.BillingCycle, caution[0].WindowType);
        Assert.Equal(UsageLevel.Caution, caution[0].Level);
        Assert.Empty(repeat);
        Assert.Single(danger);
        Assert.Equal(UsageLevel.Danger, danger[0].Level);
    }

    [Fact]
    public void BillingCycle_ResetRearmsThresholdsWithoutResetNotification()
    {
        var evaluator = new UsageNotificationEvaluator();
        evaluator.Evaluate(State(UsageWindowType.BillingCycle, 0.60, Now.AddDays(1), Now), Now);
        evaluator.Evaluate(State(UsageWindowType.BillingCycle, 0.72, Now.AddDays(1), Now.AddMinutes(1)), Now);

        var reset = evaluator.Evaluate(
            State(UsageWindowType.BillingCycle, 0.05, Now.AddDays(31), Now.AddDays(1)),
            Now.AddDays(1));
        var nextCycle = evaluator.Evaluate(
            State(UsageWindowType.BillingCycle, 0.71, Now.AddDays(31), Now.AddDays(1).AddMinutes(1)),
            Now.AddDays(1).AddMinutes(1));

        Assert.Empty(reset);
        Assert.Single(nextCycle);
        Assert.Equal(UsageNotificationKind.Threshold, nextCycle[0].Kind);
        Assert.Equal(UsageLevel.Caution, nextCycle[0].Level);
    }

    [Fact]
    public void ResetBaseline_ReenabledAboveThreshold_DoesNotCatchUp()
    {
        var evaluator = new UsageNotificationEvaluator();
        evaluator.Evaluate(State(UsageWindowType.Weekly, 0.50, Now.AddDays(4), Now), Now);

        evaluator.ResetBaseline();
        var result = evaluator.Evaluate(
            State(UsageWindowType.Weekly, 0.80, Now.AddDays(4), Now.AddHours(1)),
            Now.AddHours(1));

        Assert.Empty(result);
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
        Assert.Equal("한도가 초기화되어 현재 98% 남았습니다.", reset[0].Message);
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

    private static UsageState TwoFiveHour(double first, double second, DateTimeOffset captured)
    {
        var reset = captured.AddHours(4);
        var snapshot = new UsageSnapshot
        {
            ToolName = "Antigravity",
            CapturedAt = captured,
            Windows =
            [
                new UsageWindow { Id = "gemini-5h", Type = UsageWindowType.FiveHour, UsedRatio = first, Label = "Gemini 5h", ResetTime = reset },
                new UsageWindow { Id = "3p-5h", Type = UsageWindowType.FiveHour, UsedRatio = second, Label = "Claude+GPT 5h", ResetTime = reset },
            ],
        };
        return new UsageState
        {
            LastUpdatedAt = captured,
            Tools = [new CachedUsage { ToolName = snapshot.ToolName, Snapshot = snapshot, LastUpdatedAt = captured }],
        };
    }

    // Claude reports an idle window with a null reset time, so a used-up window comes back as
    // 0% with none and only gets its time again on first use. Recorded history shows this for
    // most resets of the 5-hour window.
    [Fact]
    public void AResetIntoAWindowWithNoResetTimeStillCountsAsAReset()
    {
        var evaluator = new UsageNotificationEvaluator();
        evaluator.Evaluate(State(UsageWindowType.FiveHour, .95, Now.AddHours(1), Now), Now);

        var toast = evaluator.Evaluate(State(UsageWindowType.FiveHour, 0, null, Now.AddHours(2)), Now);

        Assert.Equal(UsageNotificationKind.Reset, Assert.Single(toast).Kind);
    }

    [Fact]
    public void ThresholdsAreReArmedAfterAResetThatPassedThroughNoResetTime()
    {
        var evaluator = new UsageNotificationEvaluator();
        evaluator.Evaluate(State(UsageWindowType.FiveHour, .85, Now.AddHours(1), Now), Now);
        Assert.Single(evaluator.Evaluate(State(UsageWindowType.FiveHour, .95, Now.AddHours(1), Now.AddMinutes(5)), Now));

        evaluator.Evaluate(State(UsageWindowType.FiveHour, 0, null, Now.AddHours(2)), Now);
        Assert.Empty(evaluator.Evaluate(State(UsageWindowType.FiveHour, .03, Now.AddHours(7), Now.AddHours(2.5)), Now));

        var crossing = evaluator.Evaluate(State(UsageWindowType.FiveHour, .92, Now.AddHours(7), Now.AddHours(4)), Now);

        Assert.Equal(UsageNotificationKind.Threshold, Assert.Single(crossing).Kind);
    }

    [Fact]
    public void AnOldHighWaterMarkCanNotFakeAResetToastInALaterCycle()
    {
        var evaluator = new UsageNotificationEvaluator();
        evaluator.Evaluate(State(UsageWindowType.FiveHour, .95, Now.AddHours(1), Now), Now);
        evaluator.Evaluate(State(UsageWindowType.FiveHour, 0, null, Now.AddHours(2)), Now);
        evaluator.Evaluate(State(UsageWindowType.FiveHour, .25, Now.AddHours(7), Now.AddHours(3)), Now);

        // This cycle never came near the threshold, so its reset is not worth announcing.
        var nextCycle = evaluator.Evaluate(State(UsageWindowType.FiveHour, .03, Now.AddHours(13), Now.AddHours(8)), Now);

        Assert.Empty(nextCycle);
    }

    [Fact]
    public void AStrongDropWithTheResetTimeReturningAlsoCounts()
    {
        var evaluator = new UsageNotificationEvaluator();
        evaluator.Evaluate(State(UsageWindowType.FiveHour, .95, null, Now), Now);

        var toast = evaluator.Evaluate(State(UsageWindowType.FiveHour, .02, Now.AddHours(5), Now.AddHours(1)), Now);

        Assert.Equal(UsageNotificationKind.Reset, Assert.Single(toast).Kind);
    }

    [Fact]
    public void AMissingResetTimeWithoutAStrongDropIsNotAReset()
    {
        var evaluator = new UsageNotificationEvaluator();
        evaluator.Evaluate(State(UsageWindowType.FiveHour, .95, Now.AddHours(1), Now), Now);

        // Schema drift dropping the field must not read as a new cycle on its own.
        Assert.Empty(evaluator.Evaluate(State(UsageWindowType.FiveHour, .90, null, Now.AddMinutes(5)), Now));
        Assert.Empty(evaluator.Evaluate(State(UsageWindowType.FiveHour, .95, Now.AddHours(1), Now.AddMinutes(10)), Now));
    }

    [Fact]
    public void ACycleThatAdvancedWithoutADropReArmsWithoutAToast()
    {
        var evaluator = new UsageNotificationEvaluator();
        evaluator.Evaluate(State(UsageWindowType.Weekly, .60, Now.AddDays(1), Now), Now);
        Assert.Single(evaluator.Evaluate(State(UsageWindowType.Weekly, .75, Now.AddDays(1), Now.AddHours(1)), Now));

        // A new cycle that by the next poll is back near the old reading: 75% -> 66% is under
        // the drop that would make this a reset, and no toast is due for it.
        Assert.Empty(evaluator.Evaluate(State(UsageWindowType.Weekly, .66, Now.AddDays(8), Now.AddDays(2)), Now));

        var crossing = evaluator.Evaluate(State(UsageWindowType.Weekly, .72, Now.AddDays(8), Now.AddDays(3)), Now);

        Assert.Equal(UsageNotificationKind.Threshold, Assert.Single(crossing).Kind);
    }

    [Fact]
    public void ResetTimeJitterDoesNotReArmAThresholdAlreadyAnnounced()
    {
        var evaluator = new UsageNotificationEvaluator();
        evaluator.Evaluate(State(UsageWindowType.Weekly, .60, Now.AddDays(1), Now), Now);
        Assert.Single(evaluator.Evaluate(State(UsageWindowType.Weekly, .75, Now.AddDays(1), Now.AddHours(1)), Now));

        Assert.Empty(evaluator.Evaluate(State(UsageWindowType.Weekly, .66, Now.AddDays(1).AddMinutes(30), Now.AddHours(2)), Now));
        Assert.Empty(evaluator.Evaluate(State(UsageWindowType.Weekly, .72, Now.AddDays(1).AddMinutes(30), Now.AddHours(3)), Now));
    }

    [Fact]
    public void ASnapshotRestoredFromDiskIsNotABaseline()
    {
        // Off since Monday at 65%. Comparing Wednesday's first live 75% with it would announce
        // a crossing that happened while the app was not running.
        var evaluator = new UsageNotificationEvaluator();
        Assert.Empty(evaluator.Evaluate(State(UsageWindowType.Weekly, .65, Now.AddDays(4), Now, rehydrated: true), Now));

        var firstLive = evaluator.Evaluate(State(UsageWindowType.Weekly, .75, Now.AddDays(4), Now.AddDays(2)), Now);
        var nextCrossing = evaluator.Evaluate(State(UsageWindowType.Weekly, .92, Now.AddDays(4), Now.AddDays(2).AddHours(1)), Now);

        Assert.Empty(firstLive);
        Assert.Equal(UsageLevel.Danger, Assert.Single(nextCrossing).Level);
    }

    [Fact]
    public void AResetWhileTheAppWasOffIsNotAnnounced()
    {
        var evaluator = new UsageNotificationEvaluator();
        evaluator.Evaluate(State(UsageWindowType.FiveHour, .95, Now.AddHours(1), Now, rehydrated: true), Now);

        Assert.Empty(evaluator.Evaluate(State(UsageWindowType.FiveHour, .02, Now.AddHours(25), Now.AddDays(1)), Now));
    }

    [Fact]
    public void ResetExpiryAlertsOncePerDeadlineFromLiveObservations()
    {
        var evaluator = new UsageNotificationEvaluator();
        var expiry = Now.AddDays(2);
        IReadOnlyList<UsageNotification> Observe(int hour, DateTimeOffset? deadline, bool failed = false, bool rehydrated = false)
            => evaluator.Evaluate(State(UsageWindowType.Weekly, .6, Now.AddDays(7), Now.AddHours(hour),
                failed: failed, rehydrated: rehydrated, creditExpiry: deadline), Now.AddHours(hour));

        Assert.Empty(Observe(-1, expiry));
        Assert.Empty(Observe(0, expiry, failed: true));
        Assert.Empty(Observe(0, expiry, rehydrated: true));
        var alert = Assert.Single(Observe(0, expiry));
        Assert.Equal(UsageNotificationKind.ResetCredits, alert.Kind);
        Assert.Equal(UsageLevel.Caution, alert.Level);
        Assert.Null(alert.WindowType);
        Assert.Empty(Observe(1, expiry));
        Assert.Empty(Observe(49, expiry));
        Assert.Empty(Observe(50, null));
        Assert.Equal(UsageLevel.Danger, Assert.Single(Observe(51, Now.AddHours(55))).Level);
    }

    private static UsageState State(
        UsageWindowType type,
        double ratio,
        DateTimeOffset? reset,
        DateTimeOffset captured,
        bool failed = false,
        string? id = null,
        string? groupLabel = null,
        bool rehydrated = false,
        string? identity = null,
        DateTimeOffset? creditExpiry = null)
    {
        var snapshot = new UsageSnapshot
        {
            ToolName = "Codex",
            HistoryIdentity = identity,
            CapturedAt = captured,
            ResetCreditDeadlines = creditExpiry is { } expiry ? [new(expiry)] : null,
            Windows =
            [
                new UsageWindow
                {
                    Id = id,
                    GroupLabel = groupLabel,
                    Type = type,
                    UsedRatio = ratio,
                    Label = type switch
                    {
                        UsageWindowType.FiveHour => "5시간",
                        UsageWindowType.Weekly => "주간",
                        UsageWindowType.BillingCycle => "사용량",
                        _ => type.ToString(),
                    },
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
                    IsRehydrated = rehydrated,
                },
            ],
        };
    }
}
