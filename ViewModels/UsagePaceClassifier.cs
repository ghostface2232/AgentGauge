using Gauge.Localization;
using Gauge.Models;

namespace Gauge.ViewModels;

/// <summary>
/// Compares reported utilization with the fraction of a fixed window that has elapsed.
/// This is a display hint, not a provider measurement: it appears only when usage is at
/// least ten percentage points ahead of an even pace, avoiding noisy micro-warnings.
/// </summary>
public static class UsagePaceClassifier
{
    private const double MinimumAheadRatio = 0.10;
    private const double DangerAheadRatio = 0.25;

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
        var aheadRatio = Math.Clamp(window.UsedRatio, 0.0, 1.0) - elapsedRatio;
        if (aheadRatio < MinimumAheadRatio)
        {
            return (string.Empty, UsageLevel.Ok);
        }

        var level = aheadRatio >= DangerAheadRatio ? UsageLevel.Danger : UsageLevel.Caution;
        return (Loc.Get("Pace_FastDepletion"), level);
    }
}
