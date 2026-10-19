import Foundation

/// Signals the periodic cadence adapts to: Low Power Mode and a locked session.
public struct AdaptiveRefreshSignals: Sendable, Equatable {
    public var lowPowerMode: Bool
    public var sessionLocked: Bool

    public init(lowPowerMode: Bool = false, sessionLocked: Bool = false) {
        self.lowPowerMode = lowPowerMode
        self.sessionLocked = sessionLocked
    }
}

public enum AdaptiveRefreshReason: String, Sendable {
    case constrained
    case recentInteraction
    case warm
    case idle
    case longIdle
}

public struct AdaptiveRefreshDecision: Sendable, Equatable {
    public let multiplier: Double
    public let reason: AdaptiveRefreshReason

    static let cap: TimeInterval = 30 * 60

    /// Stretches a provider's base cadence, capped at 30 minutes but never below the base
    /// itself, so a future base longer than the cap is not shortened.
    public func interval(for base: TimeInterval) -> TimeInterval {
        max(base, min(base * multiplier, Self.cap))
    }
}

/// Pure cadence policy: the longer since the panel was last opened, the less often periodic
/// refreshes run, since nobody is looking. Only the periodic tick uses it; opening the panel,
/// a manual refresh, a wake or a sign-in still read immediately.
public enum AdaptiveRefreshPolicy {
    public static func evaluate(sincePanelOpened age: TimeInterval?, signals: AdaptiveRefreshSignals) -> AdaptiveRefreshDecision {
        if signals.lowPowerMode || signals.sessionLocked { return .init(multiplier: 10, reason: .constrained) }
        guard let age else { return .init(multiplier: 10, reason: .longIdle) }
        if age <= 5 * 60 { return .init(multiplier: 1, reason: .recentInteraction) }
        if age <= 3600 { return .init(multiplier: 2, reason: .warm) }
        if age < 4 * 3600 { return .init(multiplier: 4, reason: .idle) }
        return .init(multiplier: 10, reason: .longIdle)
    }
}
