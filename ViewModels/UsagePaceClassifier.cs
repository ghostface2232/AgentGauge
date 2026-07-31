using Gauge.Localization;
using Gauge.Models;

namespace Gauge.ViewModels;

/// <summary>
/// Projects whether a fixed usage window will be exhausted before its reset from the
/// average rate so far. This is a display hint, not a provider measurement. It stays
/// quiet until usage is meaningfully ahead and enough of the cycle has passed, then
/// presents one compact status label rather than exposing a noisy time estimate.
/// </summary>
public static class UsagePaceClassifier
{
    private const double MinimumAheadRatio = 0.10;
    private const double DangerAheadRatio = 0.25;
    private const double MinimumUsedRatio = 0.05;

    public static (string Text, UsageLevel Level) ForRow(
        UsageWindow window,
        DateTimeOffset? now = null)
    {
        if (window.Duration is not { } duration
            || duration <= TimeSpan.Zero
            || window.ResetTime is not { } reset)
        {
            return (string.Empty, UsageLevel.Ok);
        }

        var current = now ?? DateTimeOffset.Now;
        var start = reset - duration;
        var elapsed = current - start;
        if (elapsed <= TimeSpan.Zero || elapsed >= duration)
        {
            return (string.Empty, UsageLevel.Ok);
        }

        var elapsedRatio = elapsed.TotalSeconds / duration.TotalSeconds;
        var usedRatio = Math.Clamp(window.UsedRatio, 0.0, 1.0);
        var minimumElapsed = TimeSpan.FromTicks(Math.Min(
            TimeSpan.FromHours(12).Ticks,
            Math.Max(TimeSpan.FromMinutes(30).Ticks, (long)(duration.Ticks * 0.05))));
        if (elapsed < minimumElapsed || usedRatio < MinimumUsedRatio || usedRatio >= 1.0)
        {
            return (string.Empty, UsageLevel.Ok);
        }

        var aheadRatio = usedRatio - elapsedRatio;
        if (aheadRatio < MinimumAheadRatio)
        {
            return (string.Empty, UsageLevel.Ok);
        }

        // Average usage rate from the current provider-reported cycle start. Because the
        // warning is already gated by a meaningful lead, this estimate is less noisy than
        // showing an ETA for every small change.
        var secondsToExhaustion = elapsed.TotalSeconds * (1.0 - usedRatio) / usedRatio;
        var timeUntilReset = reset - current;
        if (!double.IsFinite(secondsToExhaustion)
            || secondsToExhaustion <= 0
            || secondsToExhaustion >= timeUntilReset.TotalSeconds)
        {
            return (string.Empty, UsageLevel.Ok);
        }

        var level = aheadRatio >= DangerAheadRatio ? UsageLevel.Danger : UsageLevel.Caution;
        return (Loc.Get("Pace_FastDepletion"), level);
    }
}
