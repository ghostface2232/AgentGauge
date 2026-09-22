namespace Gauge.Models;

/// <summary>
/// How one window's consumption has fallen across the days of the week, accumulated from
/// the usage history: <see cref="Weights"/> holds, per <see cref="DayOfWeek"/> (Sunday
/// first), the positive utilization increases recorded on that local weekday, averaged
/// over the distinct dates that weekday was observed (so a weekday seen three times does
/// not outweigh one seen twice), and <see cref="DaysObserved"/> counts the distinct local
/// dates that contributed readings. The weights are relative — only their ratios matter.
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

    // Structural equality over the weights, not reference equality on the list: the store
    // hands out a fresh snapshot on every read, and a row that receives the same profile
    // again must not re-derive its caption or notify for a change that did not happen.
    public bool Equals(UsageWeekdayProfile? other)
        => other is not null && DaysObserved == other.DaysObserved && Weights.SequenceEqual(other.Weights);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(DaysObserved);
        foreach (var weight in Weights) hash.Add(weight);
        return hash.ToHashCode();
    }
}
