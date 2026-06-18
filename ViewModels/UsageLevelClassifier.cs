using Gauge.Models;

namespace Gauge.ViewModels;

/// <summary>
/// Maps a usage ratio (0–1) to a <see cref="UsageLevel"/>. The boundaries are named
/// constants so they are easy to tune in one place.
/// </summary>
public static class UsageLevelClassifier
{
    public const double CautionThreshold = 0.75;
    public const double DangerThreshold = 0.90;

    public static UsageLevel Classify(double ratio)
        => ratio >= DangerThreshold ? UsageLevel.Danger
         : ratio >= CautionThreshold ? UsageLevel.Caution
         : UsageLevel.Ok;
}
