using Gauge.Models;

namespace Gauge.ViewModels;

public readonly record struct BurndownPoint(double X, double Remaining, double? IdealRemaining, UsageSample Sample);

public static class UsageBurndown
{
    public const int MinimumSamples = 3;
    public static readonly TimeSpan Lookback = TimeSpan.FromHours(6);

    public static IReadOnlyList<BurndownPoint> Build(UsageWindow window, IReadOnlyList<UsageSample> samples, DateTimeOffset now)
    {
        var valid = samples.Where(s => s.CapturedAt <= now && s.CapturedAt >= now - Lookback
            && double.IsFinite(s.UsedRatio) && s.UsedRatio is >= 0 and <= 1)
            .OrderBy(s => s.CapturedAt).DistinctBy(s => s.CapturedAt).ToList();
        // The cycle start is known whenever the snapshot carries a reset and a duration, and it
        // keeps filtering pre-cycle readings even after a stale snapshot crosses its reset; only
        // the ideal line needs the cycle to still be live, since it is drawn against that reset.
        DateTimeOffset? start = window is { ResetTime: { } reset, Duration: { } duration }
            && duration > TimeSpan.Zero && reset - now <= duration
            && duration.Ticks <= reset.UtcTicks ? reset - duration : null;
        var ideal = start is not null && window.ResetTime > now;
        if (start is { } cycleStart) valid.RemoveAll(s => s.CapturedAt < cycleStart);
        // Never join the end of an old allowance to the beginning of a fresh one. Only a
        // real cycle boundary splits: a provider recomputing its own utilization slightly
        // downward must not cost the row its history.
        for (var i = valid.Count - 1; i > 0; i--)
            if (UsageCycleBoundary.IsReset(valid[i - 1], valid[i]))
            {
                valid = valid.Skip(i).ToList();
                break;
            }
        if (valid.Count < MinimumSamples) return [];
        var span = (valid[^1].CapturedAt - valid[0].CapturedAt).TotalSeconds;
        if (span <= 0) return [];
        return valid.Select(s => new BurndownPoint(
            (s.CapturedAt - valid[0].CapturedAt).TotalSeconds / span,
            1 - s.UsedRatio,
            ideal && start is { } origin ? Math.Clamp(1 - (s.CapturedAt - origin).TotalSeconds / window.Duration!.Value.TotalSeconds, 0, 1) : null,
            s)).ToList();
    }

    public static BurndownPoint? Nearest(IReadOnlyList<BurndownPoint> points, double x) =>
        points.Count == 0 || !double.IsFinite(x) ? null : points.MinBy(p => Math.Abs(p.X - Math.Clamp(x, 0, 1)));
}
