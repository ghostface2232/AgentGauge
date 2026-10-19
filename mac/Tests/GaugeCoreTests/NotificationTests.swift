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
}
