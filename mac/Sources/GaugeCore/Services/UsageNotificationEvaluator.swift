import Foundation

public enum UsageNotificationKind: Sendable {
    case threshold
    case reset
}

public struct UsageNotification: Sendable, Equatable {
    public var kind: UsageNotificationKind
    public var level: UsageLevel
    public var tool: ToolKind
    public var windowID: String
    public var title: String
    public var message: String
}

/// Detects threshold crossings and resets from consecutive states — a pure function of
/// its inputs, free of UI and timing side effects. The first observation of a window is a
/// baseline, so launching above a threshold never replays an old alert. Thresholds reuse
/// the usage levels: 5-hour windows alert at danger only; weekly and billing-cycle windows
/// at caution and danger, once per cycle, re-armed by a reset.
public struct UsageNotificationEvaluator: Sendable {
    /// A reset-time advance smaller than this is server recompute jitter, not a new cycle.
    static let minimumResetAdvance: TimeInterval = 60
    private static let resetDropMinimum = 0.10

    private var observations: [Key: Observation] = [:]
    /// Tools just shown again, whose next snapshot is the coordinator's cache from before
    /// they were hidden.
    private var resumingTools = Set<ToolKind>()
    /// Windows whose baseline came from such a snapshot: the next newer observation replaces
    /// it rather than being compared with it, so a reset or crossing that happened while the
    /// tool was hidden is never replayed when it comes back.
    private var resumeBaseline = Set<Key>()

    public init() {}

    /// Forget everything, so the next state is a fresh baseline (used when alerts are
    /// re-enabled, so crossings from the silent period are never replayed).
    public mutating func resetBaseline() {
        observations.removeAll()
        resumeBaseline.removeAll()
        resumingTools.removeAll()
    }

    /// A hidden tool was shown again. Hidden tools are not emitted, so their history is
    /// already gone; this makes sure the stale cache that arrives first cannot become the
    /// baseline the first live read is compared against.
    public mutating func toolShown(_ tool: ToolKind) {
        resumingTools.insert(tool)
    }

    public mutating func evaluate(_ states: [ToolState], now: Date) -> [UsageNotification] {
        var notifications: [UsageNotification] = []
        var present = Set<Key>()

        for state in states {
            guard let snapshot = state.snapshot else { continue }
            let windows = snapshot.windows.filter(Self.isSupported)

            // A failed refresh re-serves the last good snapshot: keep its keys alive without
            // evaluating cached values as new transitions.
            if state.lastRefreshFailed {
                windows.forEach { present.insert(Key(tool: snapshot.tool, window: $0.id)) }
                continue
            }

            let resuming = resumingTools.remove(snapshot.tool) != nil
            for window in windows {
                let key = Key(tool: snapshot.tool, window: window.id)
                present.insert(key)
                guard let previous = observations[key], !resuming else {
                    observations[key] = .baseline(window, capturedAt: snapshot.capturedAt)
                    if resuming { resumeBaseline.insert(key) }
                    continue
                }
                // Re-emitted (cached/debounced) snapshots cannot be a new transition.
                guard snapshot.capturedAt > previous.capturedAt else { continue }
                if resumeBaseline.remove(key) != nil {
                    observations[key] = .baseline(window, capturedAt: snapshot.capturedAt)
                    continue
                }

                let reset = Self.isReset(previous, window)
                if reset, Self.supportsResetNotification(window.type),
                   previous.highestRatio >= Self.minimumThreshold(window.type) {
                    notifications.append(Self.resetNotification(snapshot.tool, window))
                }

                var crossed: Set<Double> = reset ? [] : previous.crossed
                for threshold in Self.thresholds(window.type)
                where !crossed.contains(threshold) && previous.ratio < threshold && window.usedRatio >= threshold {
                    notifications.append(Self.thresholdNotification(snapshot.tool, window, threshold: threshold, now: now))
                    crossed.insert(threshold)
                }
                // A polling gap that spans a reset into an already-high cycle is marked
                // consumed rather than replayed.
                if reset { crossed = Self.crossedAt(window) }

                observations[key] = Observation(
                    ratio: window.usedRatio,
                    highestRatio: max(reset ? window.usedRatio : previous.highestRatio, window.usedRatio),
                    resetTime: window.resetTime,
                    capturedAt: snapshot.capturedAt,
                    crossed: crossed)
            }
        }

        // A tool that is hidden or removed drops its history; showing it again is a fresh baseline.
        observations = observations.filter { present.contains($0.key) }
        resumeBaseline.formIntersection(present)
        return notifications
    }

    private static func isSupported(_ window: UsageWindow) -> Bool {
        window.type == .fiveHour || window.type == .weekly || window.type == .billingCycle
    }

    private static func supportsResetNotification(_ type: UsageWindowType) -> Bool {
        type == .fiveHour || type == .weekly
    }

    /// Reset timestamps are the authoritative cycle identity. The fallback covers providers
    /// that omit them but requires a strong high-to-low drop — a toast cannot be taken back.
    private static func isReset(_ previous: Observation, _ current: UsageWindow) -> Bool {
        let dropped = current.usedRatio <= previous.ratio - resetDropMinimum
        if let old = previous.resetTime, let new = current.resetTime {
            return new.timeIntervalSince(old) > minimumResetAdvance && dropped
        }
        return previous.resetTime == nil && current.resetTime == nil
            && previous.ratio >= minimumThreshold(current.type) && current.usedRatio < 0.20
    }

    private static func thresholds(_ type: UsageWindowType) -> [Double] {
        type == .fiveHour ? [UsageLevel.dangerThreshold] : [UsageLevel.cautionThreshold, UsageLevel.dangerThreshold]
    }

    private static func minimumThreshold(_ type: UsageWindowType) -> Double {
        type == .fiveHour ? UsageLevel.dangerThreshold : UsageLevel.cautionThreshold
    }

    private static func crossedAt(_ window: UsageWindow) -> Set<Double> {
        Set(thresholds(window.type).filter { window.usedRatio >= $0 })
    }

    private static func thresholdNotification(_ tool: ToolKind, _ window: UsageWindow, threshold: Double, now: Date) -> UsageNotification {
        UsageNotification(
            kind: .threshold,
            level: threshold >= UsageLevel.dangerThreshold ? .danger : .caution,
            tool: tool,
            windowID: window.id,
            title: NotificationText.thresholdTitle(tool: tool, window: window, percent: Int((threshold * 100).rounded())),
            message: ResetTimeFormatter.notification(window.resetTime, now: now))
    }

    private static func resetNotification(_ tool: ToolKind, _ window: UsageWindow) -> UsageNotification {
        UsageNotification(
            kind: .reset,
            level: .ok,
            tool: tool,
            windowID: window.id,
            title: NotificationText.resetTitle(tool: tool, window: window),
            message: NotificationText.resetMessage(availablePercent: min(max((1 - window.usedRatio) * 100, 0), 100)))
    }

    private struct Key: Hashable, Sendable {
        var tool: ToolKind
        var window: String
    }

    private struct Observation: Sendable {
        var ratio: Double
        var highestRatio: Double
        var resetTime: Date?
        var capturedAt: Date
        var crossed: Set<Double>

        static func baseline(_ window: UsageWindow, capturedAt: Date) -> Observation {
            Observation(ratio: window.usedRatio, highestRatio: window.usedRatio, resetTime: window.resetTime,
                        capturedAt: capturedAt, crossed: UsageNotificationEvaluator.crossedAt(window))
        }
    }
}
