namespace Gauge.Models;

/// <summary>
/// How one window's consumption has fallen across the days of the week, accumulated from
/// the usage history: <see cref="Weights"/> holds, per <see cref="DayOfWeek"/> (Sunday
/// first), the summed positive utilization increases recorded on that local weekday, and
/// <see cref="DaysObserved"/> counts the distinct local dates that contributed readings.
/// The weights are relative — only their ratios matter — so a window that is refilled and
/// drained many times simply accumulates a taller profile of the same shape.
/// </summary>
public sealed record UsageWeekdayProfile(IReadOnlyList<double> Weights, int DaysObserved)
{
    /// <summary>
    /// Distinct local dates with readings before the profile is trusted to shape a pace
    /// curve. Two full weeks sees every weekday twice, so a single unusual day cannot
    /// dominate; under that the automatic model reads as the uniform one.
    /// </summary>
    public const int MinimumDaysObserved = 14;

    /// <summary>
    /// True when the profile has enough history behind it and describes some consumption
    /// at all — a window never drawn on has no shape to lend.
    /// </summary>
    public bool IsSufficient => DaysObserved >= MinimumDaysObserved && Weights.Count == 7 && Weights.Sum() > 0;
}
