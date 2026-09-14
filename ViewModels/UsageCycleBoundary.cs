using Gauge.Models;

namespace Gauge.ViewModels;

/// <summary>
/// Decides where a recorded sample series crosses from one quota cycle into the next.
/// Shared by the sparkline and the ETA projection so both segment the same history the
/// same way; a boundary only one of them saw would draw a fresh cycle under a slope fitted
/// across the old one.
///
/// A bare decrease is deliberately NOT a boundary. Providers recompute utilization between
/// polls (42.1% → 41.9% on the same cycle), and every consumer here reacts to a boundary by
/// discarding the readings before it, so treating rounding noise as a reset silently throws
/// away a cycle's history.
/// </summary>
public static class UsageCycleBoundary
{
    /// <summary>
    /// Ratio decrease large enough to identify a reset on its own. This is the only signal
    /// left for providers that expose no reset time, so it has to clear any plausible
    /// recalculation.
    /// </summary>
    public const double DropThreshold = 0.05;

    /// <summary>
    /// Reset-time advance that counts as a new cycle, absorbing the small jitter providers
    /// show when they recompute the same reset instant.
    /// </summary>
    private static readonly TimeSpan MinimumResetAdvance = TimeSpan.FromMinutes(1);

    /// <summary>
    /// True when <paramref name="current"/> belongs to a later cycle than
    /// <paramref name="previous"/>: either the provider moved the reset timestamp on and
    /// usage fell with it — the authoritative cycle identity, so any drop counts — or usage
    /// fell far enough that no recalculation explains it.
    /// </summary>
    public static bool IsReset(UsageSample previous, UsageSample current)
    {
        if (current.UsedRatio <= previous.UsedRatio - DropThreshold)
        {
            return true;
        }
        return current.UsedRatio < previous.UsedRatio
            && previous.ResetTime is { } oldReset
            && current.ResetTime is { } newReset
            && newReset > oldReset + MinimumResetAdvance;
    }
}
