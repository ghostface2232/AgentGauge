using Gauge.Localization;
using Gauge.Models;
using Gauge.ViewModels;

namespace Gauge.Services;

/// <summary>
/// Detects threshold crossings and quota resets from consecutive normalized states.
/// The first observation establishes a baseline, so launching Gauge above a threshold
/// never replays an old alert.
/// </summary>
public sealed class UsageNotificationEvaluator
{
    private const double ResetDropMinimum = 0.10;

    private readonly Dictionary<WindowKey, Observation> _observations = new();

    /// <summary>
    /// Drops all prior observations so the next state establishes a fresh baseline.
    /// Use this when notifications are re-enabled to avoid replaying crossings that
    /// happened while the user had notifications turned off.
    /// </summary>
    public void ResetBaseline() => _observations.Clear();

    public IReadOnlyList<UsageNotification> Evaluate(UsageState state, DateTimeOffset now)
    {
        var notifications = new List<UsageNotification>();
        var present = new HashSet<WindowKey>();

        foreach (var tool in state.Tools)
        {
            if (tool.Snapshot is not { } snapshot)
            {
                continue;
            }

            // A failed refresh retains the coordinator's last-good snapshot. Keep
            // its keys alive without evaluating cached values as new transitions.
            if (tool.LastRefreshFailed)
            {
                foreach (var cachedWindow in snapshot.Windows.Where(IsSupportedWindow))
                {
                    present.Add(new WindowKey(snapshot.ToolName, cachedWindow.Key));
                }
                continue;
            }

            foreach (var window in snapshot.Windows.Where(IsSupportedWindow))
            {
                var key = new WindowKey(snapshot.ToolName, window.Key);
                present.Add(key);

                if (!_observations.TryGetValue(key, out var previous))
                {
                    _observations[key] = Observation.CreateBaseline(window, snapshot.CapturedAt);
                    continue;
                }

                // Cached snapshots are re-emitted for debounce and provider cache hits.
                // They cannot represent a new transition.
                if (snapshot.CapturedAt <= previous.CapturedAt)
                {
                    continue;
                }

                var reset = IsReset(previous, window);
                if (reset
                    && SupportsResetNotification(window.Type)
                    && previous.HighestRatio >= MinimumThreshold(window.Type))
                {
                    notifications.Add(CreateReset(snapshot.ToolName, window, now));
                }

                var crossed = reset
                    ? ThresholdMask.None
                    : previous.Crossed;

                foreach (var threshold in ThresholdsFor(window.Type))
                {
                    var mask = MaskFor(threshold);
                    if (!crossed.HasFlag(mask)
                        && previous.Ratio < threshold
                        && window.UsedRatio >= threshold)
                    {
                        notifications.Add(CreateThreshold(snapshot.ToolName, window, threshold, now));
                        crossed |= mask;
                    }
                }

                // If a polling gap spans a reset and the new cycle is already above a
                // threshold, mark it consumed instead of presenting stale crossings.
                if (reset)
                {
                    crossed = MaskAtRatio(window.Type, window.UsedRatio);
                }

                _observations[key] = new Observation(
                    window.UsedRatio,
                    Math.Max(reset ? window.UsedRatio : previous.HighestRatio, window.UsedRatio),
                    window.ResetTime,
                    snapshot.CapturedAt,
                    crossed);
            }
        }

        // Removing a tool resets its alert history. Re-adding it establishes a fresh baseline.
        foreach (var stale in _observations.Keys.Where(key => !present.Contains(key)).ToList())
        {
            _observations.Remove(stale);
        }

        return notifications;
    }

    private static bool IsSupportedWindow(UsageWindow window)
        => window.Type is UsageWindowType.FiveHour
            or UsageWindowType.Weekly
            or UsageWindowType.BillingCycle;

    private static bool SupportsResetNotification(UsageWindowType type)
        => type is UsageWindowType.FiveHour or UsageWindowType.Weekly;

    private static bool IsReset(Observation previous, UsageWindow current)
    {
        var resetTimeAdvanced = previous.ResetTime is { } oldReset
            && current.ResetTime is { } newReset
            && newReset > oldReset.AddMinutes(1);
        var usageDropped = current.UsedRatio <= previous.Ratio - ResetDropMinimum;

        // Reset timestamps are the authoritative cycle identity. The fallback covers
        // providers that temporarily omit them, but requires a strong high-to-low drop.
        return (resetTimeAdvanced && usageDropped)
            || (previous.ResetTime is null && current.ResetTime is null
                && previous.Ratio >= MinimumThreshold(current.Type)
                && current.UsedRatio < 0.20);
    }

    private static IEnumerable<double> ThresholdsFor(UsageWindowType type)
        => type == UsageWindowType.FiveHour
            ? [UsageLevelClassifier.DangerThreshold]
            : [UsageLevelClassifier.CautionThreshold, UsageLevelClassifier.DangerThreshold];

    private static double MinimumThreshold(UsageWindowType type)
        => type == UsageWindowType.FiveHour
            ? UsageLevelClassifier.DangerThreshold
            : UsageLevelClassifier.CautionThreshold;

    private static ThresholdMask MaskAtRatio(UsageWindowType type, double ratio)
    {
        var mask = ThresholdMask.None;
        foreach (var threshold in ThresholdsFor(type))
        {
            if (ratio >= threshold) mask |= MaskFor(threshold);
        }
        return mask;
    }

    private static ThresholdMask MaskFor(double threshold)
        => threshold >= UsageLevelClassifier.DangerThreshold
            ? ThresholdMask.Danger
            : ThresholdMask.Caution;

    private static UsageNotification CreateThreshold(
        string toolName, UsageWindow window, double threshold, DateTimeOffset now)
    {
        var percent = (int)Math.Round(threshold * 100);
        return new UsageNotification
        {
            Kind = UsageNotificationKind.Threshold,
            Level = threshold >= UsageLevelClassifier.DangerThreshold
                ? UsageLevel.Danger
                : UsageLevel.Caution,
            ToolName = toolName,
            WindowType = window.Type,
            Title = NotificationText.ThresholdTitle(toolName, window.Type, percent),
            Message = ResetTimeFormatter.ForNotification(window.ResetTime, now),
            CreatedAt = now,
        };
    }

    private static UsageNotification CreateReset(
        string toolName, UsageWindow window, DateTimeOffset now)
    {
        var availablePercent = Math.Clamp((1 - window.UsedRatio) * 100, 0, 100);
        return new UsageNotification
        {
            Kind = UsageNotificationKind.Reset,
            Level = UsageLevel.Ok,
            ToolName = toolName,
            WindowType = window.Type,
            Title = NotificationText.ResetTitle(toolName, window.Type),
            Message = NotificationText.ResetMessage(availablePercent),
            CreatedAt = now,
        };
    }

    // Keyed by the window's provider-stable Key, not its Type, so a tool with two windows
    // of the same Type (e.g. Antigravity's two 5-hour limits) tracks each independently.
    private readonly record struct WindowKey(string ToolName, string Window);

    private sealed record Observation(
        double Ratio,
        double HighestRatio,
        DateTimeOffset? ResetTime,
        DateTimeOffset CapturedAt,
        ThresholdMask Crossed)
    {
        public static Observation CreateBaseline(UsageWindow window, DateTimeOffset capturedAt) => new(
            window.UsedRatio,
            window.UsedRatio,
            window.ResetTime,
            capturedAt,
            MaskAtRatio(window.Type, window.UsedRatio));
    }

    [Flags]
    private enum ThresholdMask
    {
        None = 0,
        Caution = 1,
        Danger = 2,
    }
}
