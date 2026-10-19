import Foundation

/// How the pace caption spreads a multi-day window's quota over its cycle — the curve the
/// row's actual utilization is compared against. `uniform` is the original model: every
/// hour of the cycle is expected to consume the same share. It calls a user who works
/// Monday to Friday "in deficit" all week, because it expects the weekend's share to have
/// been spent too. `workDays` expects nothing on Saturday and Sunday, so the expected curve
/// is flat over the weekend. `automatic` shapes the curve from the recorded per-weekday
/// consumption once enough of it exists, and reads as `uniform` until then. App-wide.
/// Windows shorter than two days (the 5-hour sessions) always use the uniform spread.
///
/// The raw value is the persisted setting, so it must never change for an existing case.
public enum WeeklyPaceModel: String, CaseIterable, Sendable, Identifiable {
    case uniform
    case workDays
    case automatic

    public var id: String { rawValue }

    public var displayName: String {
        switch self {
        case .uniform: L10n.get("PaceModel_Uniform")
        case .workDays: L10n.get("PaceModel_WorkDays")
        case .automatic: L10n.get("PaceModel_Automatic")
        }
    }
}

/// How one window's consumption has fallen across the days of the week, accumulated from
/// the usage history: `weights` holds, per weekday (Sunday first), the positive utilization
/// increases recorded on that local weekday, averaged over the distinct dates that weekday
/// was observed (so a weekday seen three times does not outweigh one seen twice), and
/// `daysObserved` counts the distinct local dates that contributed readings. The weights
/// are relative — only their ratios matter. Equality is structural, so a row handed the
/// same profile again need not re-derive its caption.
public struct UsageWeekdayProfile: Sendable, Hashable {
    /// Distinct local dates with readings before the profile is trusted to shape a pace
    /// curve. Two full weeks sees every weekday twice, so a single unusual day cannot
    /// dominate; under that the automatic model reads as the uniform one.
    public static let minimumDaysObserved = 14

    public var weights: [Double]
    public var daysObserved: Int

    public init(weights: [Double], daysObserved: Int) {
        self.weights = weights
        self.daysObserved = daysObserved
    }

    /// True when the profile has enough history behind it and describes some consumption
    /// at all — a window never drawn on has no shape to lend.
    public var isSufficient: Bool {
        daysObserved >= Self.minimumDaysObserved && weights.count == 7 && weights.reduce(0, +) > 0
    }
}

/// The share of a cycle that has "elapsed" when each local weekday carries its own weight —
/// the arithmetic behind the work-day and automatic pace models. A weight of zero on a day
/// means no consumption is expected then, so the expected curve is flat across it; the
/// uniform model is the special case of every weight equal.
public enum WeeklyPaceTimeline {
    /// Weights for a Monday-to-Friday working week, indexed Sunday first.
    public static let workDayWeights: [Double] = [0, 1, 1, 1, 1, 1, 0]

    /// Shortest window a weekday model applies to. A model that distinguishes days has
    /// nothing to say inside a single afternoon, and applying it there would only make a
    /// 5-hour session that straddles midnight into Saturday read as frozen.
    public static let minimumDuration: TimeInterval = 2 * 86_400

    /// Longest window a weekday model applies to — a year. It keeps the day-by-day
    /// integration short; anything longer uses the uniform spread.
    public static let maximumDuration: TimeInterval = 366 * 86_400

    /// The weighted time from `start` to `now` as a share of the weighted time from `start`
    /// to `end`, where each instant is weighted by `weights[its local weekday]` in
    /// `calendar`'s time zone. Nil when the whole cycle carries no weight (nothing is
    /// expected at any point, so no pace can be read off it) — the caller falls back to the
    /// uniform spread.
    public static func elapsedRatio(
        start: Date, now: Date, end: Date, weights: [Double], calendar: Calendar
    ) -> Double? {
        guard weights.count == 7, end > start else { return nil }
        let total = weightedSeconds(from: start, to: end, weights: weights, calendar: calendar)
        guard total > 0 else { return nil }
        let clamped = min(max(now, start), end)
        let elapsed = weightedSeconds(from: start, to: clamped, weights: weights, calendar: calendar)
        return min(max(elapsed / total, 0), 1)
    }

    // Integrates the weight over [from, to] one local calendar day at a time, so a segment
    // never straddles a weekday change.
    private static func weightedSeconds(from: Date, to: Date, weights: [Double], calendar: Calendar) -> Double {
        var sum = 0.0
        var cursor = from
        while cursor < to {
            // A local day lasts 23–25 hours, so 26 hours past its start always lands in the
            // next one, and startOfDay of that is the next day's first instant. Adding one
            // day to the start instead keeps its wall-clock hour, which skips the real
            // midnight after a day that began at 01:00 (a spring-forward at midnight), and
            // startOfDay picks the first of two midnights where a zone falls back at
            // midnight, so the repeated hour is credited to the new weekday.
            let dayStart = calendar.startOfDay(for: cursor)
            var next = calendar.startOfDay(for: dayStart.addingTimeInterval(26 * 3600))
            // A zone transition could still derive a boundary at or before the cursor;
            // stepping an hour keeps the walk finite without skipping a day boundary.
            if next <= cursor { next = cursor.addingTimeInterval(3600) }
            let segmentEnd = min(next, to)
            let weight = weights[calendar.component(.weekday, from: cursor) - 1]
            if weight > 0 { sum += weight * segmentEnd.timeIntervalSince(cursor) }
            cursor = segmentEnd
        }
        return sum
    }
}
