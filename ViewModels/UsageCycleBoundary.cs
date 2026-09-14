using Gauge.Models;

namespace Gauge.ViewModels;

/// <summary>
/// Decides where a recorded sample series crosses from one quota cycle into the next.
/// Shared by the sparkline and the ETA projection so both segment the same history the
/// same way; a boundary only one of them saw would draw a fresh cycle under a slope fitted
/// across the old one.
///
/// The reset timestamp the provider reported with each reading is the cycle's identity, so
/// it decides whenever both readings carry one; only when schema drift leaves one without a
/// reset time does the size of the decrease stand in. A decrease alone is deliberately never
/// a boundary: providers recompute utilization between polls (42.1% → 41.9% on the same
/// cycle), and every consumer here reacts to a boundary by discarding the readings before
/// it, so reading recalculation as a reset silently throws away a cycle's history.
/// </summary>
public static class UsageCycleBoundary
{
    /// <summary>
    /// Ratio decrease large enough to identify a reset on its own. This is the only signal
    /// left once the reset timestamps cannot answer, so it has to clear any plausible
    /// recalculation.
    /// </summary>
    private const double DropThreshold = 0.05;

    /// <summary>
    /// Reset-time advance that counts as a new cycle, absorbing the small jitter providers
    /// show when they recompute the same reset instant. Shared with
    /// <c>UsageNotificationEvaluator</c>, which tolerates the same jitter.
    /// </summary>
    public static readonly TimeSpan MinimumResetAdvance = TimeSpan.FromMinutes(1);

    /// <summary>
    /// True when <paramref name="current"/> belongs to a later cycle than
    /// <paramref name="previous"/>.
    /// </summary>
    public static bool IsReset(UsageSample previous, UsageSample current)
    {
        if (previous.ResetTime is { } oldReset && current.ResetTime is { } newReset)
        {
            // Two readings naming the same reset instant are one cycle however far apart
            // their ratios sit, so the size of a decrease decides nothing here. Every
            // provider reports an absolute reset from its payload, and a large unexplained
            // decrease under an unchanged one is the provider recomputing its own headline
            // percent — Cursor picks that percent from a precedence chain whose shape can
            // change between polls (see CursorProvider.ParsePlanPercentUsed).
            return newReset > oldReset + MinimumResetAdvance
                && current.UsedRatio < previous.UsedRatio;
        }
        // Schema drift left at least one reading without a cycle identity; the size of the
        // decrease is the only evidence left.
        return current.UsedRatio <= previous.UsedRatio - DropThreshold;
    }
}
