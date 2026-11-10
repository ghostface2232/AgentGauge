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

    // Far past recompute jitter (seconds in recorded history) and the same bar
    // UsageWeekdayProfile uses, because nothing here can tell a re-armed alert from a repeat.
    private static readonly TimeSpan CycleAdvanceMinimum = TimeSpan.FromHours(1);

    private readonly Dictionary<WindowKey, Observation> _observations = new();
    private readonly Dictionary<string, DateTimeOffset> _deadlineCaptures = new();
    private readonly HashSet<(string ToolName, DateTimeOffset ExpiresAt)> _warnedDeadlines = new();

    /// <summary>
    /// Drops all prior observations so the next state establishes a fresh baseline.
    /// Use this when notifications are re-enabled to avoid replaying crossings that
    /// happened while the user had notifications turned off.
    /// </summary>
    public void ResetBaseline()
    {
        _observations.Clear();
        _deadlineCaptures.Clear();
        _resumeBaseline.Clear();
    }

    private readonly HashSet<string> _hidden = new();
    private readonly HashSet<WindowKey> _resumeBaseline = new();

    public void SetToolHidden(string toolName, bool hidden)
    {
        if (hidden)
        {
            _hidden.Add(toolName);
            foreach (var key in _observations.Keys.Where(k => k.ToolName == toolName))
                _resumeBaseline.Add(key);
        }
        else _hidden.Remove(toolName);
    }

    public IReadOnlyList<UsageNotification> Evaluate(UsageState state, DateTimeOffset now)
    {
        var notifications = new List<UsageNotification>();
        var present = new HashSet<WindowKey>();
        var presentDeadlineTools = new HashSet<string>();

        foreach (var tool in state.Tools)
        {
            if (tool.Snapshot is not { } snapshot)
            {
                continue;
            }

            presentDeadlineTools.Add(snapshot.ToolName);
            if (_hidden.Contains(tool.ToolName))
            {
                ObserveResetDeadline(snapshot, hidden: true, now, notifications);
                // Track the newest snapshot seen while hidden (e.g. an in-flight request
                // that completed after hiding). Otherwise, after showing, a cache re-serve
                // of that snapshot would look newer than the stale baseline, consume the
                // resume baseline, and let the first live poll replay an old crossing.
                foreach (var window in snapshot.Windows.Where(IsSupportedWindow))
                {
                    var key = new WindowKey(snapshot.ToolName, window.Key);
                    if (!_observations.TryGetValue(key, out var existing)
                        || snapshot.CapturedAt > existing.CapturedAt)
                    {
                        _observations[key] = Observation.CreateBaseline(window, snapshot.CapturedAt);
                    }
                }
                foreach (var key in _observations.Keys.Where(k => k.ToolName == tool.ToolName))
                {
                    present.Add(key);
                    _resumeBaseline.Add(key);
                }
                continue;
            }

            // Restored from disk at startup: it can be days old, so comparing the first live
            // reading against it would present everything that happened while the app was off.
            // The first live reading sets the baseline instead.
            if (tool.IsRehydrated)
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

            ObserveResetDeadline(snapshot, hidden: false, now, notifications);
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

                // A cache re-serve cannot consume the first-live baseline after showing a tool.
                if (_resumeBaseline.Remove(key))
                {
                    _observations[key] = Observation.CreateBaseline(window, snapshot.CapturedAt);
                    continue;
                }

                var reset = IsReset(previous, window);
                if (reset
                    && SupportsResetNotification(window.Type)
                    && previous.HighestRatio >= MinimumThreshold(window.Type))
                {
                    notifications.Add(CreateReset(snapshot.ToolName, window, now));
                }

                // A new cycle re-arms its thresholds even when no reset toast is due: usage can
                // already be back above the old reading by the next poll, and a stale mask or
                // high-water mark would silence this cycle's alerts and fake a later reset toast.
                var newCycle = reset || CycleAdvanced(previous, window);
                var crossed = newCycle
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
                if (newCycle)
                {
                    crossed = MaskAtRatio(window.Type, window.UsedRatio);
                }

                _observations[key] = new Observation(
                    window.UsedRatio,
                    Math.Max(newCycle ? window.UsedRatio : previous.HighestRatio, window.UsedRatio),
                    window.ResetTime,
                    snapshot.CapturedAt,
                    crossed);
            }
        }

        // Removing a tool resets its alert history. Re-adding it establishes a fresh baseline.
        foreach (var stale in _observations.Keys.Where(key => !present.Contains(key)).ToList())
        {
            _observations.Remove(stale);
            _resumeBaseline.Remove(stale);
        }

        foreach (var stale in _deadlineCaptures.Keys.Where(tool => !presentDeadlineTools.Contains(tool)).ToList())
            _deadlineCaptures.Remove(stale);
        _warnedDeadlines.RemoveWhere(deadline => deadline.ExpiresAt <= now);

        return notifications;
    }

    private void ObserveResetDeadline(
        UsageSnapshot snapshot, bool hidden, DateTimeOffset now, List<UsageNotification> notifications)
    {
        if (_deadlineCaptures.TryGetValue(snapshot.ToolName, out var previous) && snapshot.CapturedAt <= previous)
            return;
        _deadlineCaptures[snapshot.ToolName] = snapshot.CapturedAt;
        if (hidden || ResetCreditDeadline.Upcoming(snapshot.ResetCreditDeadlines, now) is not { } deadline
            || !_warnedDeadlines.Add((snapshot.ToolName, deadline.ExpiresAt))) return;

        notifications.Add(new UsageNotification
        {
            Kind = UsageNotificationKind.ResetCredits,
            Level = deadline.ExpiresAt - now <= TimeSpan.FromDays(1) ? UsageLevel.Danger : UsageLevel.Caution,
            ToolName = snapshot.ToolName,
            Title = Loc.Format("Notif_ResetCreditsTitle", snapshot.ToolName),
            Message = ResetCreditText.Description(deadline),
            CreatedAt = now,
        });
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
            && newReset > oldReset + UsageCycleBoundary.MinimumResetAdvance;
        var usageDropped = current.UsedRatio <= previous.Ratio - ResetDropMinimum;

        // Reset timestamps are the authoritative cycle identity. The fallback covers readings
        // missing one on EITHER side, but requires a strong high-to-low drop. Claude really does
        // this: an idle window reports a reset time of null, so a used-up window comes back as
        // 0% with none and only gets its time again on the first use. The shape matches
        // UsageCycleBoundary (timestamps decide; magnitude only stands in for them) while staying
        // stricter: a toast fires once and cannot be taken back, where a sparkline segmented
        // wrongly re-draws on the next reading.
        return (resetTimeAdvanced && usageDropped)
            || ((previous.ResetTime is null || current.ResetTime is null)
                && previous.Ratio >= MinimumThreshold(current.Type)
                && current.UsedRatio < 0.20);
    }

    private static bool CycleAdvanced(Observation previous, UsageWindow current)
        => previous.ResetTime is { } oldReset
            && current.ResetTime is { } newReset
            && newReset >= oldReset + CycleAdvanceMinimum;

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
            Title = NotificationText.ThresholdTitle(toolName, window, percent),
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
            Title = NotificationText.ResetTitle(toolName, window),
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
