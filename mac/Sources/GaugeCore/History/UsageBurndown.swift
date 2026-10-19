import Foundation

/// The data behind a bar row's remaining-quota sparkline: the last six hours of one
/// window's recorded readings in the current cycle, normalized onto a shared time axis,
/// plus the dashed "ideal" remaining line where the window's duration and reset are valid.
/// Pure data; the view draws it.
public struct UsageBurndown: Sendable, Equatable {
    public struct Point: Sendable, Equatable {
        /// Position on the time axis, 0 at the oldest plotted reading and 1 at the newest.
        public var x: Double
        /// Remaining fraction of the quota at this reading, in [0, 1].
        public var remaining: Double
        /// Remaining fraction an even spend over the cycle would leave at this reading's
        /// time; nil when the cycle is not known or no longer live.
        public var idealRemaining: Double?
        public var sample: UsageSample
    }

    /// Fewer readings than this draw nothing.
    public static let minimumSamples = 3
    /// Trailing span of history the sparkline covers; also the lookback to ask the store for.
    public static let lookback: TimeInterval = 6 * 3600

    /// At least `minimumSamples` points, oldest first.
    public let points: [Point]

    /// Whether every point carries an ideal value, so the dashed line can be drawn.
    public var hasIdealLine: Bool { points.allSatisfy { $0.idealRemaining != nil } }

    /// The plot for `window`, or nil when there are not enough usable readings to draw one.
    public static func build(window: UsageWindow, samples: [UsageSample], now: Date = Date()) -> UsageBurndown? {
        let cutoff = now.addingTimeInterval(-lookback)
        // Ordered by capture time with ties kept in input order, then one reading per instant.
        var valid: [UsageSample] = []
        for sample in samples.enumerated()
            .filter({ $0.element.capturedAt <= now && $0.element.capturedAt >= cutoff
                && $0.element.usedRatio.isFinite && (0...1).contains($0.element.usedRatio) })
            .sorted(by: { ($0.element.capturedAt, $0.offset) < ($1.element.capturedAt, $1.offset) })
            .map(\.element)
        where valid.last?.capturedAt != sample.capturedAt {
            valid.append(sample)
        }

        // The cycle start is known whenever the window carries a reset and a duration, and it
        // keeps filtering pre-cycle readings even after a stale snapshot crosses its reset;
        // only the ideal line needs the cycle to still be live, since it is drawn against
        // that reset.
        var cycleStart: Date?
        var duration: TimeInterval = 0
        if let reset = window.resetTime, let length = window.duration, length > 0,
           reset.timeIntervalSince(now) <= length {
            cycleStart = reset.addingTimeInterval(-length)
            duration = length
        }
        let showsIdeal = cycleStart != nil && (window.resetTime.map { $0 > now } ?? false)
        if let cycleStart { valid.removeAll { $0.capturedAt < cycleStart } }

        // Never join the end of an old allowance to the beginning of a fresh one. Only a
        // real cycle boundary splits: a provider recomputing its own utilization slightly
        // downward must not cost the row its history.
        if let boundary = valid.indices.dropFirst().last(where: {
            UsageCycleBoundary.isReset(valid[$0 - 1], valid[$0])
        }) {
            valid = Array(valid[boundary...])
        }

        guard valid.count >= minimumSamples, let first = valid.first, let last = valid.last else { return nil }
        let span = last.capturedAt.timeIntervalSince(first.capturedAt)
        guard span > 0 else { return nil }
        return UsageBurndown(points: valid.map { sample in
            var ideal: Double?
            if showsIdeal, let cycleStart {
                ideal = min(max(1 - sample.capturedAt.timeIntervalSince(cycleStart) / duration, 0), 1)
            }
            return Point(
                x: sample.capturedAt.timeIntervalSince(first.capturedAt) / span,
                remaining: 1 - sample.usedRatio,
                idealRemaining: ideal,
                sample: sample)
        })
    }

    /// The point nearest a normalized pointer position over the plot (clamped to 0…1).
    public func nearest(toX x: Double) -> Point? {
        guard x.isFinite else { return nil }
        let target = min(max(x, 0), 1)
        return points.min { abs($0.x - target) < abs($1.x - target) }
    }

    /// The hover caption that temporarily replaces the row's reset text: the nearest
    /// reading's local time and its remaining percent ("14:05 · 62% 남음").
    public func hoverText(atX x: Double, calendar: Calendar = .current) -> String? {
        guard let point = nearest(toX: x) else { return nil }
        let formatter = DateFormatter()
        formatter.locale = L10n.locale
        formatter.timeZone = calendar.timeZone
        formatter.dateFormat = "HH:mm"
        let percent = Int((point.remaining * 100).rounded(.toNearestOrAwayFromZero))
        return L10n.format("Burndown_Hover", formatter.string(from: point.sample.capturedAt), percent)
    }
}
