import Foundation
import XCTest
@testable import GaugeCore

final class NotificationEvaluatorTests: XCTestCase {
    private let start = Date(timeIntervalSince1970: 1_000_000)

    private func state(_ ratio: Double, type: UsageWindowType = .fiveHour, reset: Date?, at captured: Date) -> ToolState {
        let window = UsageWindow(type: type, usedRatio: ratio, resetTime: reset, duration: 5 * 3600)
        return ToolState(tool: .claude, snapshot: UsageSnapshot(tool: .claude, plan: nil, windows: [window], capturedAt: captured))
    }

    func testBaselineNeverAlerts() {
        var evaluator = UsageNotificationEvaluator()
        XCTAssertTrue(evaluator.evaluate([state(0.95, reset: start, at: start)], now: start).isEmpty)
    }

    func testFiveHourAlertsAtDangerOnceAndIgnoresReemits() {
        var evaluator = UsageNotificationEvaluator()
        let reset = start.addingTimeInterval(3600)
        _ = evaluator.evaluate([state(0.50, reset: reset, at: start)], now: start)
        XCTAssertTrue(evaluator.evaluate([state(0.75, reset: reset, at: start + 60)], now: start + 60).isEmpty)
        let alerts = evaluator.evaluate([state(0.92, reset: reset, at: start + 120)], now: start + 120)
        XCTAssertEqual(alerts.map(\.kind), [.threshold])
        XCTAssertEqual(alerts.first?.title, "Claude · 5시간 한도 90% 사용")
        XCTAssertTrue(evaluator.evaluate([state(0.95, reset: reset, at: start + 180)], now: start + 180).isEmpty)
        // Same capture time: a cached re-emit is never a transition.
        XCTAssertTrue(evaluator.evaluate([state(0.10, reset: reset.addingTimeInterval(18_000), at: start + 180)], now: start + 200).isEmpty)
    }

    func testResetAfterHighUsageNotifiesAndRearms() {
        var evaluator = UsageNotificationEvaluator()
        let reset = start.addingTimeInterval(3600)
        _ = evaluator.evaluate([state(0.95, reset: reset, at: start)], now: start)
        let next = reset.addingTimeInterval(5 * 3600)
        let alerts = evaluator.evaluate([state(0.02, reset: next, at: start + 4000)], now: start + 4000)
        XCTAssertEqual(alerts.map(\.kind), [.reset])
        let again = evaluator.evaluate([state(0.91, reset: next, at: start + 5000)], now: start + 5000)
        XCTAssertEqual(again.map(\.kind), [.threshold])
    }

    func testWeeklyAlertsAtCautionAndDanger() {
        var evaluator = UsageNotificationEvaluator()
        let reset = start.addingTimeInterval(86_400)
        _ = evaluator.evaluate([state(0.60, type: .weekly, reset: reset, at: start)], now: start)
        let alerts = evaluator.evaluate([state(0.93, type: .weekly, reset: reset, at: start + 60)], now: start + 60)
        XCTAssertEqual(alerts.map(\.level), [.caution, .danger])
    }

    func testFailedRefreshIsNotEvaluated() {
        var evaluator = UsageNotificationEvaluator()
        let reset = start.addingTimeInterval(3600)
        _ = evaluator.evaluate([state(0.50, reset: reset, at: start)], now: start)
        var failed = state(0.95, reset: reset, at: start + 60)
        failed.lastRefreshFailed = true
        XCTAssertTrue(evaluator.evaluate([failed], now: start + 60).isEmpty)
    }
}

final class MenuBarSummaryTests: XCTestCase {
    func testHighestWindowDrivesLevel() {
        let snapshot = UsageSnapshot(tool: .codex, plan: nil, windows: [
            UsageWindow(type: .fiveHour, usedRatio: 0.2),
            UsageWindow(type: .weekly, usedRatio: 0.75),
        ], capturedAt: Date())
        let summary = MenuBarSummary(states: [ToolState(tool: .codex, snapshot: snapshot)])
        XCTAssertEqual(summary.level, .caution)
        XCTAssertEqual(summary.percentText(basis: .used), "75%")
        XCTAssertEqual(summary.percentText(basis: .remaining), "25%")
        XCTAssertNil(MenuBarSummary(states: []).level)
    }

    func testTargetKeySelectsSpecificWindow() {
        let codexSnapshot = UsageSnapshot(tool: .codex, plan: nil, windows: [
            UsageWindow(type: .fiveHour, usedRatio: 0.2),
            UsageWindow(type: .weekly, usedRatio: 0.75),
        ], capturedAt: Date())
        let claudeSnapshot = UsageSnapshot(tool: .claude, plan: nil, windows: [
            UsageWindow(type: .fiveHour, usedRatio: 0.95),
            UsageWindow(type: .weekly, usedRatio: 0.1),
        ], capturedAt: Date())
        let states = [
            ToolState(tool: .codex, snapshot: codexSnapshot),
            ToolState(tool: .claude, snapshot: claudeSnapshot),
        ]

        // Select codex 5-hour (0.2), even though claude 5h is 0.95 and codex weekly is 0.75
        let summary1 = MenuBarSummary(states: states, targetKey: "codex:fiveHour")
        XCTAssertEqual(summary1.level, .ok)
        XCTAssertEqual(summary1.percentText(basis: .used), "20%")

        // Select claude weekly (0.1)
        let summary2 = MenuBarSummary(states: states, targetKey: "claude:weekly")
        XCTAssertEqual(summary2.level, .ok)
        XCTAssertEqual(summary2.percentText(basis: .used), "10%")

        // Non-existent targetKey falls back to max (0.95)
        let summaryFallback = MenuBarSummary(states: states, targetKey: "cursor:billingCycle")
        XCTAssertEqual(summaryFallback.level, .danger)
        XCTAssertEqual(summaryFallback.percentText(basis: .used), "95%")

        // Empty/nil targetKey falls back to max (0.95)
        let summaryAll = MenuBarSummary(states: states, targetKey: "")
        XCTAssertEqual(summaryAll.level, .danger)
        XCTAssertEqual(summaryAll.percentText(basis: .used), "95%")
    }
}

final class HiddenToolResumeTests: XCTestCase {
    private func state(_ ratio: Double, reset: Date, at captured: Date) -> ToolState {
        var state = ToolState(tool: .claude, snapshot: UsageSnapshot(tool: .claude, plan: nil, windows: [
            UsageWindow(type: .weekly, usedRatio: ratio, resetTime: reset, duration: 7 * 86_400),
        ], capturedAt: captured))
        state.auth = .signedIn
        return state
    }

    /// Hidden at 80%, reset while hidden: the stale cache shown first must not become the
    /// baseline the first live read is compared with, or a reset toast would replay.
    func testShowingAToolAgainNeverReplaysWhatHappenedWhileHidden() {
        var evaluator = UsageNotificationEvaluator()
        let start = Date(timeIntervalSince1970: 1_800_000_000)
        let oldReset = start.addingTimeInterval(3600)
        XCTAssertTrue(evaluator.evaluate([state(0.80, reset: oldReset, at: start)], now: start).isEmpty)

        // Hidden: not emitted at all.
        XCTAssertTrue(evaluator.evaluate([], now: start.addingTimeInterval(60)).isEmpty)

        evaluator.toolShown(.claude)
        let later = start.addingTimeInterval(8 * 86_400)
        // The coordinator re-emits its pre-hide cache first…
        XCTAssertTrue(evaluator.evaluate([state(0.80, reset: oldReset, at: start)], now: later).isEmpty)
        // …then the live read lands in a fresh cycle.
        let fresh = state(0.05, reset: later.addingTimeInterval(7 * 86_400), at: later)
        XCTAssertTrue(evaluator.evaluate([fresh], now: later).isEmpty, "no reset toast for a reset that happened while hidden")

        // From then on, transitions are real again.
        let crossing = state(0.75, reset: later.addingTimeInterval(7 * 86_400), at: later.addingTimeInterval(3600))
        XCTAssertEqual(evaluator.evaluate([crossing], now: later.addingTimeInterval(3600)).map(\.kind), [.threshold])
    }
}

final class StringsTableTests: XCTestCase {
    /// The merge keeps the first table's row, so a key in two tables makes the second dead.
    func testEveryKeyLivesInOneTable() {
        let tables = [Strings.core, Strings.features, Strings.antigravity, Strings.apiCost, Strings.history]
        var seen = Set<String>()
        for table in tables {
            for key in table.keys {
                XCTAssertTrue(seen.insert(key).inserted, "duplicate string key \(key)")
            }
        }
        for (key, row) in Strings.table {
            XCTAssertEqual(row.count, 3, key)
            XCTAssertFalse(row.contains(where: \.isEmpty), key)
        }
    }
}
