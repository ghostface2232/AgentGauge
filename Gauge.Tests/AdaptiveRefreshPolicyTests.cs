using Gauge.Models;
using Gauge.Services;

namespace Gauge.Tests;

public sealed class AdaptiveRefreshPolicyTests
{
    [Theory]
    [InlineData(null, 10, AdaptiveRefreshReason.LongIdle)]
    [InlineData(-1, 1, AdaptiveRefreshReason.RecentInteraction)]
    [InlineData(0, 1, AdaptiveRefreshReason.RecentInteraction)]
    [InlineData(300, 1, AdaptiveRefreshReason.RecentInteraction)]
    [InlineData(301, 2, AdaptiveRefreshReason.Warm)]
    [InlineData(3600, 2, AdaptiveRefreshReason.Warm)]
    [InlineData(3601, 4, AdaptiveRefreshReason.Idle)]
    [InlineData(14399, 4, AdaptiveRefreshReason.Idle)]
    [InlineData(14400, 10, AdaptiveRefreshReason.LongIdle)]
    public void PolicyBoundariesAreDeterministic(int? seconds, int multiplier, AdaptiveRefreshReason reason)
    {
        var decision = AdaptiveRefreshPolicy.Evaluate(seconds.HasValue ? TimeSpan.FromSeconds(seconds.Value) : null, default);
        Assert.Equal(new(multiplier, reason), decision);
        foreach (var tool in ToolCatalog.All)
        {
            var baseline = UsageCoordinator.PeriodicIntervalFor(tool.Kind);
            Assert.InRange(decision.IntervalFor(baseline), baseline, TimeSpan.FromMinutes(30));
        }
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void ConstraintsWinOverRecentInteraction(bool saver, bool locked)
    {
        var decision = AdaptiveRefreshPolicy.Evaluate(TimeSpan.Zero, new(saver, locked));
        Assert.Equal(AdaptiveRefreshReason.Constrained, decision.Reason);
        Assert.All(ToolCatalog.All, d => Assert.Equal(TimeSpan.FromMinutes(30),
            decision.IntervalFor(UsageCoordinator.PeriodicIntervalFor(d.Kind))));
        Assert.Equal(TimeSpan.FromMinutes(45), decision.IntervalFor(TimeSpan.FromMinutes(45)));
    }
}
