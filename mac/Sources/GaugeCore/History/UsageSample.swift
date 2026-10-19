import Foundation

/// One recorded usage reading for a window, as stored in the usage history. Ordered by
/// capture time; consumed by the sparkline and the ETA projection.
///
/// `resetTime` is the reset the provider reported with this reading. It is the
/// authoritative identity of the cycle the reading belongs to (see `UsageCycleBoundary`),
/// and is nil for a provider that reports none.
public struct UsageSample: Sendable, Hashable {
    public var capturedAt: Date
    public var usedRatio: Double
    public var resetTime: Date?

    public init(capturedAt: Date, usedRatio: Double, resetTime: Date? = nil) {
        self.capturedAt = capturedAt
        self.usedRatio = usedRatio
        self.resetTime = resetTime
    }
}

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
public enum UsageCycleBoundary {
    /// Ratio decrease large enough to identify a reset on its own. This is the only signal
    /// left once the reset timestamps cannot answer, so it has to clear any plausible
    /// recalculation.
    static let dropThreshold = 0.05

    /// Reset-time advance that counts as a new cycle, absorbing the small jitter providers
    /// show when they recompute the same reset instant.
    public static let minimumResetAdvance: TimeInterval = 60

    /// True when `current` belongs to a later cycle than `previous`.
    public static func isReset(_ previous: UsageSample, _ current: UsageSample) -> Bool {
        if let oldReset = previous.resetTime, let newReset = current.resetTime {
            // Two readings naming the same reset instant are one cycle however far apart
            // their ratios sit: a large unexplained decrease under an unchanged reset is the
            // provider recomputing its own headline percent (Cursor picks it from a
            // precedence chain whose shape can change between polls).
            return newReset > oldReset.addingTimeInterval(minimumResetAdvance)
                && current.usedRatio < previous.usedRatio
        }
        // Schema drift left at least one reading without a cycle identity; the size of the
        // decrease is the only evidence left.
        return current.usedRatio <= previous.usedRatio - dropThreshold
    }
}
