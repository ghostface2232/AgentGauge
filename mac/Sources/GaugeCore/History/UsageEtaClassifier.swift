import Foundation

/// Projects when a window will hit 100% from its recorded recent readings (the usage
/// history), as a restrained caption ("2시간 후 소진 예상"). Unlike `PaceClassifier` — which
/// compares utilization against the expected spend over the whole cycle — this
/// extrapolates the measured short-term burn rate, so it reacts to what the user is doing
/// right now. It refines, never replaces, the pace caption.
///
/// It stays silent unless the projection is trustworthy and actionable: at least three
/// live readings spanning 30+ minutes, a positive slope, and a projected exhaustion that
/// lands before the window's reset (running out after the reset is not a problem).
public enum UsageEtaClassifier {
    /// Trailing span of history the projection is computed from.
    public static let lookback: TimeInterval = 90 * 60

    static let minimumSpan: TimeInterval = 30 * 60
    static let minimumSamples = 3
    static let minimumUsedRatio = 0.05
    // A projection further out than this is noise from a nearly flat slope.
    static let maximumHorizon: TimeInterval = 30 * 86_400

    /// The ETA caption for a row, or nil when the projection should stay quiet.
    public static func caption(samples: [UsageSample], window: UsageWindow, now: Date = Date()) -> String? {
        let used = min(max(window.usedRatio, 0), 1)
        guard used >= minimumUsedRatio, used < 1,
              let eta = projectExhaustion(samples: samples, now: now) else { return nil }
        // Only warn when exhaustion lands before the reset; an unknown reset time gives no
        // basis to call the pace a problem, so it also stays quiet.
        guard let reset = window.resetTime, eta < reset else { return nil }
        return format(eta.timeIntervalSince(now))
    }

    /// Projected exhaustion time from a least-squares fit over the recent samples, or nil
    /// when there is no trustworthy upward trend.
    public static func projectExhaustion(samples: [UsageSample], now: Date) -> Date? {
        let cutoff = now.addingTimeInterval(-lookback)
        let recent = samples.filter { $0.capturedAt >= cutoff && $0.capturedAt <= now }
        guard let last = recent.last else { return nil }

        var segments = [0]
        for index in recent.indices.dropFirst() {
            if recent[index].isManualReset(after: recent[index - 1]) {
                segments.append(index)
            } else if UsageCycleBoundary.isReset(recent[index - 1], recent[index]) {
                // A scheduled rollover still discards the previous cycle's readings.
                segments = [index]
            }
        }
        segments.append(recent.count)
        let slope = fitSlope(samples: recent, segments: segments)
        guard slope.isFinite, slope > 0 else { return nil }

        let secondsToFull = (1 - min(max(last.usedRatio, 0), 1)) / slope
        guard secondsToFull.isFinite, secondsToFull > 0, secondsToFull <= maximumHorizon else { return nil }

        let eta = last.capturedAt.addingTimeInterval(secondsToFull)
        // A projection already in the past means the fit overshot the clamped ratio; the
        // percent and pace caption cover that state, so stay quiet.
        return eta > now ? eta : nil
    }

    // Fit a shared rate with a separate starting level for each allowance. The reset
    // interval contributes neither usage nor time, and the projection uses the latest ratio.
    private static func fitSlope(samples: [UsageSample], segments: [Int]) -> Double {
        var observedCount = 0
        var observedSpan: TimeInterval = 0
        var covariance = 0.0, variance = 0.0
        for segment in segments.indices.dropFirst() {
            let start = segments[segment - 1]
            var end = segments[segment]
            // A window waiting at 100% before a manual reset cannot measure further demand.
            if segment < segments.count - 1 {
                while end - start >= 2, samples[end - 1].usedRatio >= 1, samples[end - 2].usedRatio >= 1 {
                    end -= 1
                }
            }
            let count = end - start
            guard count >= 2 else { continue }

            let origin = samples[start].capturedAt
            observedCount += count
            observedSpan += samples[end - 1].capturedAt.timeIntervalSince(origin)
            var sumX = 0.0, sumY = 0.0, sumXY = 0.0, sumXX = 0.0
            for sample in samples[start..<end] {
                let x = sample.capturedAt.timeIntervalSince(origin)
                let y = sample.usedRatio
                sumX += x
                sumY += y
                sumXY += x * y
                sumXX += x * x
            }
            // With one segment, keep the original arithmetic exactly.
            let n = Double(count)
            let divisor = segments.count == 2 ? 1 : n
            covariance += (n * sumXY - sumX * sumY) / divisor
            variance += (n * sumXX - sumX * sumX) / divisor
        }
        guard observedCount >= minimumSamples, observedSpan >= minimumSpan, variance > 0 else { return .nan }
        return covariance / variance
    }

    private static func format(_ interval: TimeInterval) -> String {
        // Whole seconds, so a fit landing a hair under the hour mark (floating-point
        // residue of the least-squares sums) does not read as "8h 29m" for 8h 30m.
        let untilEta = interval.rounded()
        if untilEta >= 86_400 {
            let days = max(1, Int((untilEta / 86_400).rounded(.up)))
            return L10n.format(days == 1 ? "Eta_InDay" : "Eta_InDays", days)
        }
        let hours = Int(untilEta / 3600)
        let minutes = Int(untilEta.truncatingRemainder(dividingBy: 3600) / 60)
        if hours > 0 && minutes > 0 { return L10n.format("Eta_InHoursMinutes", hours, minutes) }
        if hours > 0 { return L10n.format("Eta_InHours", hours) }
        return L10n.format("Eta_InMinutes", max(1, minutes))
    }
}
