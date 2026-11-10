import Foundation

public struct DailyUsageSegment: Sendable, Equatable {
    public let day: Date
    public let usedRatio: Double
}

public enum UsageDailyBreakdown {
    public static let lookback: TimeInterval = 8 * 86_400
    private static let maximumGap: TimeInterval = 12 * 3600

    /// Observed increases within each local day of the current weekly cycle.
    public static func build(window: UsageWindow, samples: [UsageSample], capturedAt: Date,
                             calendar: Calendar = Calendar(identifier: .gregorian)) -> [DailyUsageSegment] {
        guard window.type == .weekly, window.usedRatio.isFinite, window.usedRatio > 0,
              let reset = window.resetTime, reset > capturedAt else { return [] }
        let duration = window.duration ?? 7 * 86_400
        guard duration > 0, duration <= lookback else { return [] }
        let start = reset.addingTimeInterval(-duration)
        // The history store mirrors its database's millisecond precision.
        let captured = UsageHistoryStore.roundedToMillisecond(capturedAt)
        var readings = samples.filter { $0.capturedAt >= start && $0.capturedAt <= captured && $0.usedRatio.isFinite }
            .sorted { $0.capturedAt < $1.capturedAt }
        if readings.last?.capturedAt != captured {
            readings.append(UsageSample(capturedAt: captured, usedRatio: window.usedRatio,
                                        resetTime: reset, resetCredits: window.resetCredits))
        }
        var first = 0
        for index in readings.indices.dropFirst() {
            let previous = readings[index - 1]
            let current = readings[index]
            if UsageCycleBoundary.isReset(previous, current) || current.isManualReset(after: previous) {
                first = index
            }
        }

        var days: [Date: Double] = [:]
        var ceiling = min(window.usedRatio, 1)
        // A downward correction removes earlier usage above the latest observed level.
        for index in stride(from: readings.count - 1, to: first, by: -1) {
            let previous = readings[index - 1]
            let current = readings[index]
            let priorRatio = min(max(previous.usedRatio, 0), 1)
            let increase = max(0, ceiling - priorRatio)
            ceiling = min(ceiling, priorRatio)
            // Gaps and intervals crossing midnight cannot be attributed to a day.
            if increase > 0, current.capturedAt.timeIntervalSince(previous.capturedAt) <= maximumGap,
               calendar.isDate(current.capturedAt, inSameDayAs: previous.capturedAt) {
                days[calendar.startOfDay(for: current.capturedAt), default: 0] += increase
            }
        }
        return days.sorted { $0.key < $1.key }.map { DailyUsageSegment(day: $0.key, usedRatio: $0.value) }
    }
}
