using Gauge.Models;
using Gauge.ViewModels;

namespace Gauge.Tests;

/// <summary>
/// Burn-rate ETA projection: fires only on a trustworthy upward trend whose exhaustion
/// lands before the reset, and never averages across a cycle reset.
/// </summary>
public sealed class UsageEtaClassifierTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 3, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void SteadyBurnProjectsExhaustionCaption()
    {
        // 0.30/hour over the last hour, 60% remaining → exhausted in 2 hours.
        var samples = Samples((-60, 0.10), (-30, 0.25), (0, 0.40));
        var window = Window(used: 0.40, resetInHours: 4);

        Assert.Equal("2시간 후 소진 예상", UsageEtaClassifier.ForRow(window, samples, Now));
    }

    [Fact]
    public void QuietWhenResetComesFirst()
    {
        var samples = Samples((-60, 0.10), (-30, 0.25), (0, 0.40));
        var window = Window(used: 0.40, resetInHours: 1.5);

        Assert.Empty(UsageEtaClassifier.ForRow(window, samples, Now));
    }

    [Fact]
    public void QuietWithoutResetTime()
    {
        var samples = Samples((-60, 0.10), (-30, 0.25), (0, 0.40));
        var window = Window(used: 0.40, resetInHours: 4) with { ResetTime = null };

        Assert.Empty(UsageEtaClassifier.ForRow(window, samples, Now));
    }

    [Fact]
    public void QuietWithTooFewSamplesOrShortSpan()
    {
        Assert.Null(UsageEtaClassifier.ProjectExhaustion(Samples((-60, 0.1), (0, 0.4)), Now));
        Assert.Null(UsageEtaClassifier.ProjectExhaustion(
            Samples((-10, 0.1), (-5, 0.25), (0, 0.4)), Now));
    }

    [Fact]
    public void QuietOnFlatOrDecreasingUsage()
    {
        Assert.Null(UsageEtaClassifier.ProjectExhaustion(
            Samples((-60, 0.40), (-30, 0.40), (0, 0.40)), Now));
        Assert.Null(UsageEtaClassifier.ProjectExhaustion(
            Samples((-60, 0.40), (-30, 0.30), (0, 0.22)), Now));
    }

    [Fact]
    public void SamplesBeforeCycleResetAreDiscarded()
    {
        // Old cycle ran high, then reset; only the fresh cycle's slope counts. Averaging
        // across the drop would produce a negative slope and stay quiet.
        var samples = Samples((-80, 0.80), (-70, 0.90), (-60, 0.05), (-30, 0.10), (0, 0.15));
        var window = Window(used: 0.15, resetInHours: 10);

        Assert.Equal("8시간 30분 후 소진 예상", UsageEtaClassifier.ForRow(window, samples, Now));
    }

    [Fact]
    public void SamplesOutsideLookbackAreIgnored()
    {
        // Two of three samples are older than the 90-minute lookback → not enough left.
        var samples = Samples((-300, 0.10), (-200, 0.20), (0, 0.40));
        Assert.Null(UsageEtaClassifier.ProjectExhaustion(samples, Now));
    }

    [Fact]
    public void QuietAtVeryLowOrFullUsage()
    {
        var samples = Samples((-60, 0.001), (-30, 0.02), (0, 0.03));
        Assert.Empty(UsageEtaClassifier.ForRow(Window(used: 0.03, resetInHours: 4), samples, Now));

        var full = Samples((-60, 0.80), (-30, 0.90), (0, 1.00));
        Assert.Empty(UsageEtaClassifier.ForRow(Window(used: 1.00, resetInHours: 4), full, Now));
    }

    private static IReadOnlyList<UsageSample> Samples(params (double MinutesAgo, double Ratio)[] points)
        => points.Select(p => new UsageSample(Now.AddMinutes(p.MinutesAgo), p.Ratio)).ToList();

    private static UsageWindow Window(double used, double resetInHours) => new()
    {
        Type = UsageWindowType.FiveHour,
        Label = "5시간",
        UsedRatio = used,
        ResetTime = Now.AddHours(resetInHours),
        Duration = TimeSpan.FromHours(5),
    };
}
