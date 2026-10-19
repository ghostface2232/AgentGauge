import Foundation
import XCTest
@testable import GaugeCore

/// Where a recorded series crosses into a new quota cycle: a reset timestamp that moved on,
/// or — only once the timestamps cannot answer — a decrease too large to be the provider
/// recalculating its own utilization.
final class UsageCycleBoundaryTests: XCTestCase {
    private let now = Date(timeIntervalSince1970: 1_789_387_200) // 2026-09-14 12:00 UTC
    private var reset: Date { now.addingTimeInterval(2 * 3600) }

    private func sample(_ minutes: Double, _ ratio: Double, _ reset: Date? = nil) -> UsageSample {
        UsageSample(capturedAt: now.addingTimeInterval(minutes * 60), usedRatio: ratio, resetTime: reset)
    }

    // The magnitude of a decrease decides nothing while the reset instant stands still.
    func testUnchangedResetTimestampIsOneCycleWhateverTheRatiosDo() {
        for (before, after) in [(0.421, 0.419), (0.421, 0.421), (0.400, 0.430), (0.500, 0.451), (0.900, 0.050)] {
            XCTAssertFalse(UsageCycleBoundary.isReset(sample(-5, before, reset), sample(0, after, reset)), "\(before)→\(after)")
        }
    }

    func testResetTimestampAdvanceWithAnyDecreaseIsABoundary() {
        XCTAssertTrue(UsageCycleBoundary.isReset(sample(-5, 0.421, reset), sample(0, 0.419, reset.addingTimeInterval(5 * 3600))))
        // A reset instant recomputed by seconds is jitter, not a new cycle.
        XCTAssertFalse(UsageCycleBoundary.isReset(sample(-5, 0.421, reset), sample(0, 0.419, reset.addingTimeInterval(30))))
        // The advance alone proves nothing while usage keeps climbing.
        XCTAssertFalse(UsageCycleBoundary.isReset(sample(-5, 0.419, reset), sample(0, 0.421, reset.addingTimeInterval(5 * 3600))))
    }

    func testMissingResetTimestampsFallBackToTheSizeOfTheDrop() {
        XCTAssertTrue(UsageCycleBoundary.isReset(sample(-5, 0.90), sample(0, 0.05)))
        XCTAssertFalse(UsageCycleBoundary.isReset(sample(-5, 0.421), sample(0, 0.419)))
        // One side alone cannot establish an advance, so the drop decides there too.
        XCTAssertTrue(UsageCycleBoundary.isReset(sample(-5, 0.90, reset), sample(0, 0.05)))
        XCTAssertFalse(UsageCycleBoundary.isReset(sample(-5, 0.421), sample(0, 0.419, reset)))
    }
}

/// Burn-rate ETA projection: fires only on a trustworthy upward trend whose exhaustion
/// lands before the reset, and never averages across a cycle reset.
final class UsageEtaClassifierTests: XCTestCase {
    private let now = Date(timeIntervalSince1970: 1_785_758_400) // 2026-08-03 12:00 UTC

    override func setUp() {
        L10n.configure(.korean)
    }

    private func samples(_ points: (Double, Double)...) -> [UsageSample] {
        points.map { UsageSample(capturedAt: now.addingTimeInterval($0.0 * 60), usedRatio: $0.1) }
    }

    private func window(used: Double, resetInHours: Double?) -> UsageWindow {
        UsageWindow(
            type: .fiveHour, usedRatio: used,
            resetTime: resetInHours.map { now.addingTimeInterval($0 * 3600) }, duration: 5 * 3600)
    }

    func testSteadyBurnProjectsExhaustionCaption() {
        // 0.30/hour over the last hour, 60% remaining → exhausted in 2 hours.
        let series = samples((-60, 0.10), (-30, 0.25), (0, 0.40))
        XCTAssertEqual(UsageEtaClassifier.caption(samples: series, window: window(used: 0.40, resetInHours: 4), now: now),
                       "2시간 후 소진 예상")
    }

    func testQuietWhenResetComesFirstOrIsUnknown() {
        let series = samples((-60, 0.10), (-30, 0.25), (0, 0.40))
        XCTAssertNil(UsageEtaClassifier.caption(samples: series, window: window(used: 0.40, resetInHours: 1.5), now: now))
        XCTAssertNil(UsageEtaClassifier.caption(samples: series, window: window(used: 0.40, resetInHours: nil), now: now))
    }

    func testQuietWithTooFewSamplesOrShortSpan() {
        XCTAssertNil(UsageEtaClassifier.projectExhaustion(samples: samples((-60, 0.1), (0, 0.4)), now: now))
        XCTAssertNil(UsageEtaClassifier.projectExhaustion(samples: samples((-10, 0.1), (-5, 0.25), (0, 0.4)), now: now))
    }

    func testQuietOnFlatOrDecreasingUsage() {
        XCTAssertNil(UsageEtaClassifier.projectExhaustion(samples: samples((-60, 0.40), (-30, 0.40), (0, 0.40)), now: now))
        XCTAssertNil(UsageEtaClassifier.projectExhaustion(samples: samples((-60, 0.40), (-30, 0.30), (0, 0.22)), now: now))
    }

    func testSamplesBeforeCycleResetAreDiscarded() {
        // Old cycle ran high, then reset; only the fresh cycle's slope counts. Averaging
        // across the drop would produce a negative slope and stay quiet.
        let series = samples((-80, 0.80), (-70, 0.90), (-60, 0.05), (-30, 0.10), (0, 0.15))
        XCTAssertEqual(UsageEtaClassifier.caption(samples: series, window: window(used: 0.15, resetInHours: 10), now: now),
                       "8시간 30분 후 소진 예상")
    }

    func testResetTimestampAdvanceSplitsCyclesEvenWhenTheDropIsSmall() {
        let older = now.addingTimeInterval(3 * 3600)
        let newer = now.addingTimeInterval(8 * 3600)
        let series = [
            UsageSample(capturedAt: now.addingTimeInterval(-90 * 60), usedRatio: 0.02, resetTime: older),
            UsageSample(capturedAt: now.addingTimeInterval(-75 * 60), usedRatio: 0.04, resetTime: older),
            UsageSample(capturedAt: now.addingTimeInterval(-60 * 60), usedRatio: 0.06, resetTime: older),
            UsageSample(capturedAt: now.addingTimeInterval(-45 * 60), usedRatio: 0.02, resetTime: newer),
            UsageSample(capturedAt: now.addingTimeInterval(-30 * 60), usedRatio: 0.06, resetTime: newer),
            UsageSample(capturedAt: now, usedRatio: 0.12, resetTime: newer),
        ]
        let fresh = UsageEtaClassifier.projectExhaustion(samples: Array(series.dropFirst(3)), now: now)
        XCTAssertNotNil(fresh)
        XCTAssertEqual(fresh, UsageEtaClassifier.projectExhaustion(samples: series, now: now))
    }

    func testRecalculatedUsageWithinOneCycleKeepsTheSeries() {
        let reset = now.addingTimeInterval(3 * 3600)
        let series = [
            UsageSample(capturedAt: now.addingTimeInterval(-60 * 60), usedRatio: 0.400, resetTime: reset),
            UsageSample(capturedAt: now.addingTimeInterval(-30 * 60), usedRatio: 0.520, resetTime: reset),
            UsageSample(capturedAt: now, usedRatio: 0.440, resetTime: reset),
        ]
        XCTAssertNotNil(UsageEtaClassifier.projectExhaustion(samples: series, now: now))
    }

    func testSamplesOutsideLookbackAreIgnored() {
        XCTAssertNil(UsageEtaClassifier.projectExhaustion(samples: samples((-300, 0.10), (-200, 0.20), (0, 0.40)), now: now))
    }

    func testQuietAtVeryLowOrFullUsage() {
        XCTAssertNil(UsageEtaClassifier.caption(
            samples: samples((-60, 0.001), (-30, 0.02), (0, 0.03)), window: window(used: 0.03, resetInHours: 4), now: now))
        XCTAssertNil(UsageEtaClassifier.caption(
            samples: samples((-60, 0.80), (-30, 0.90), (0, 1.00)), window: window(used: 1.00, resetInHours: 4), now: now))
    }

    func testLongProjectionsReadInDays() {
        // 0.01/hour with 70% left → 70 hours → "3일".
        let series = samples((-60, 0.29), (-30, 0.295), (0, 0.30))
        XCTAssertEqual(UsageEtaClassifier.caption(samples: series, window: window(used: 0.30, resetInHours: 100), now: now),
                       "3일 후 소진 예상")
    }
}

final class UsageBurndownTests: XCTestCase {
    private let now = Date(timeIntervalSince1970: 1_789_387_200) // 2026-09-14 12:00 UTC
    private var window: UsageWindow {
        UsageWindow(type: .fiveHour, usedRatio: 0.6, resetTime: now.addingTimeInterval(2 * 3600), duration: 5 * 3600)
    }
    private var samples: [UsageSample] {
        [UsageSample(capturedAt: now.addingTimeInterval(-7200), usedRatio: 0.2),
         UsageSample(capturedAt: now.addingTimeInterval(-3600), usedRatio: 0.4),
         UsageSample(capturedAt: now, usedRatio: 0.6)]
    }

    override func setUp() {
        L10n.configure(.korean)
    }

    func testInsufficientHistoryLeavesNoPlot() {
        for count in 0..<3 {
            XCTAssertNil(UsageBurndown.build(window: window, samples: Array(samples.prefix(count)), now: now))
        }
    }

    func testRemainingAndIdealShareTimeAxis() throws {
        let points = try XCTUnwrap(UsageBurndown.build(window: window, samples: samples, now: now)).points
        XCTAssertEqual(points.count, 3)
        XCTAssertEqual(points[0].x, 0)
        XCTAssertEqual(points[2].x, 1)
        XCTAssertEqual(points[0].remaining, 0.8, accuracy: 1e-8)
        XCTAssertEqual(points[2].remaining, 0.4, accuracy: 1e-8)
        XCTAssertEqual(try XCTUnwrap(points[2].idealRemaining), 0.4, accuracy: 1e-8)
        XCTAssertEqual(UsageBurndown(points: points).nearest(toX: 0.55), points[1])
    }

    func testResetAndInvalidReadingsNeverJoinCycles() {
        let later = now.addingTimeInterval(1)
        XCTAssertNil(UsageBurndown.build(
            window: window, samples: samples + [UsageSample(capturedAt: later, usedRatio: 0.1)], now: later))
        XCTAssertNil(UsageBurndown.build(
            window: window, samples: [samples[0], samples[1], UsageSample(capturedAt: now, usedRatio: .nan)], now: now))
        var shifted = window
        shifted.resetTime = now.addingTimeInterval(4.5 * 3600)
        XCTAssertNil(UsageBurndown.build(window: shifted, samples: samples, now: now))
    }

    func testRecalculatedUsageKeepsHistoryButARolledCycleSplitsIt() {
        // Duration is left out so only the reset-boundary rule is under test.
        var window = self.window
        window.duration = nil
        window.usedRatio = 0.419
        let reset = now.addingTimeInterval(2 * 3600)
        func at(_ hours: Double, _ ratio: Double, _ reset: Date? = nil) -> UsageSample {
            UsageSample(capturedAt: now.addingTimeInterval(hours * 3600), usedRatio: ratio, resetTime: reset)
        }
        var series = [at(-2, 0.400, reset), at(-1, 0.421, reset), at(0, 0.419, reset)]
        // 42.1% → 41.9% on the same reset is the provider recalculating, not a new cycle.
        XCTAssertEqual(UsageBurndown.build(window: window, samples: series, now: now)?.points.count, 3)

        series[2].resetTime = reset.addingTimeInterval(5 * 3600)
        XCTAssertNil(UsageBurndown.build(window: window, samples: series, now: now))

        // Even a collapse under one unchanged reset is a recalculation, not a new cycle.
        XCTAssertEqual(UsageBurndown.build(
            window: window, samples: [at(-2, 0.40, reset), at(-1, 0.52, reset), at(0, 0.07, reset)], now: now)?.points.count, 3)

        // Without reset timestamps only a drop past the threshold identifies a new cycle.
        XCTAssertEqual(UsageBurndown.build(
            window: window, samples: [at(-2, 0.40), at(-1, 0.421), at(0, 0.419)], now: now)?.points.count, 3)
        XCTAssertNil(UsageBurndown.build(window: window, samples: [at(-2, 0.40), at(-1, 0.50), at(0, 0.44)], now: now))
    }

    func testExpiredSnapshotKeepsCycleStartFilterWithoutIdealLine() throws {
        let start = now.addingTimeInterval(-3600)
        var window = self.window
        window.resetTime = start.addingTimeInterval(5 * 3600)
        let series = [-45.0, -30, -15, 10, 20, 30].enumerated().map { index, minutes in
            UsageSample(capturedAt: start.addingTimeInterval(minutes * 60), usedRatio: Double(index + 1) / 10)
        }
        let reset = try XCTUnwrap(window.resetTime)
        let before = try XCTUnwrap(UsageBurndown.build(window: window, samples: series, now: reset.addingTimeInterval(-1)))
        XCTAssertEqual(before.points.count, 3)
        XCTAssertTrue(before.hasIdealLine)
        let after = try XCTUnwrap(UsageBurndown.build(window: window, samples: series, now: reset.addingTimeInterval(1)))
        XCTAssertEqual(after.points.map(\.sample), before.points.map(\.sample))
        XCTAssertTrue(after.points.allSatisfy { $0.idealRemaining == nil })
    }

    func testUnknownDurationOmitsIdealLine() throws {
        var window = self.window
        window.duration = nil
        let burndown = try XCTUnwrap(UsageBurndown.build(window: window, samples: samples, now: now))
        XCTAssertFalse(burndown.hasIdealLine)
        XCTAssertTrue(burndown.points.allSatisfy { $0.idealRemaining == nil })
    }

    func testHoverNamesTheNearestReadingsTimeAndRemainingPercent() throws {
        var utc = Calendar(identifier: .gregorian)
        utc.timeZone = TimeZone(identifier: "UTC")!
        let burndown = try XCTUnwrap(UsageBurndown.build(window: window, samples: samples, now: now))
        XCTAssertEqual(burndown.hoverText(atX: 1, calendar: utc), "12:00 · 40% 남음")
        XCTAssertEqual(burndown.hoverText(atX: 0.5, calendar: utc), "11:00 · 60% 남음")
        // Out-of-range positions clamp to the ends; a non-finite one shows nothing.
        XCTAssertEqual(burndown.hoverText(atX: -3, calendar: utc), "10:00 · 80% 남음")
        XCTAssertNil(burndown.hoverText(atX: .nan, calendar: utc))
    }

    func testDuplicateCaptureInstantsPlotOnce() throws {
        let burndown = try XCTUnwrap(UsageBurndown.build(window: window, samples: samples + [samples[2]], now: now))
        XCTAssertEqual(burndown.points.count, 3)
    }
}
