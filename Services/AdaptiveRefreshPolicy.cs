namespace Gauge.Services;

public enum AdaptiveRefreshReason { Constrained, RecentInteraction, Warm, Idle, LongIdle }

public readonly record struct AdaptiveRefreshSignals(bool EnergySaver, bool SessionLocked);

public readonly record struct AdaptiveRefreshDecision(int Multiplier, AdaptiveRefreshReason Reason)
{
    /// <summary>Never shorten a provider's cost floor, even if a future floor exceeds the cap.</summary>
    public TimeSpan IntervalFor(TimeSpan baseline) => TimeSpan.FromTicks((long)Math.Max(baseline.Ticks,
        Math.Min(baseline.Ticks * (double)Multiplier, TimeSpan.FromMinutes(30).Ticks)));
}

/// <summary>Pure cadence policy. The caller supplies monotonic interaction age and OS signals.</summary>
public static class AdaptiveRefreshPolicy
{
    public static AdaptiveRefreshDecision Evaluate(TimeSpan? sincePopoverOpened, AdaptiveRefreshSignals signals)
    {
        if (signals.EnergySaver || signals.SessionLocked) return new(10, AdaptiveRefreshReason.Constrained);
        if (sincePopoverOpened is not { } age) return new(10, AdaptiveRefreshReason.LongIdle);
        if (age <= TimeSpan.FromMinutes(5)) return new(1, AdaptiveRefreshReason.RecentInteraction);
        if (age <= TimeSpan.FromHours(1)) return new(2, AdaptiveRefreshReason.Warm);
        if (age < TimeSpan.FromHours(4)) return new(4, AdaptiveRefreshReason.Idle);
        return new(10, AdaptiveRefreshReason.LongIdle);
    }
}
