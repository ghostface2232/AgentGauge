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

    func testTargetKeySelectsToolRing() {
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

        // The ring carries both arcs; the percent beside it is the 5-hour one.
        let codex = MenuBarSummary(states: states, targetKey: "codex")
        XCTAssertEqual(codex.ring?.tool, .codex)
        XCTAssertEqual(codex.ring?.short?.usedRatio, 0.2)
        XCTAssertEqual(codex.ring?.long?.usedRatio, 0.75)
        XCTAssertEqual(codex.percentText(basis: .used), "20%")

        // A window target from an older build and the display name both name the tool.
        XCTAssertEqual(MenuBarSummary(states: states, targetKey: "claude:weekly").ring?.tool, .claude)
        XCTAssertEqual(MenuBarSummary(states: states, targetKey: "Claude").ring?.tool, .claude)

        // A tool with no state, and overall, fall back to the highest window (0.95).
        let missing = MenuBarSummary(states: states, targetKey: "cursor")
        XCTAssertNil(missing.ring)
        XCTAssertEqual(missing.percentText(basis: .used), "95%")
        let overall = MenuBarSummary(states: states, targetKey: "")
        XCTAssertNil(overall.ring)
        XCTAssertEqual(overall.level, .danger)
    }

    func testTargetNormalizesLegacyWindowKeys() {
        XCTAssertEqual(StatusTarget.normalize("codex:weekly"), "codex")
        XCTAssertEqual(StatusTarget.normalize("Claude"), "claude")
        XCTAssertEqual(StatusTarget.normalize(""), "")
        XCTAssertEqual(StatusTarget.normalize(nil), "")
    }
}

final class StatusRingSelectionTests: XCTestCase {
    func testAccountWideWindowsWinOverScopedOnes() {
        let ring = StatusRingSelection.select([
            UsageWindow(type: .weekly, providerID: "fable", groupLabel: "Fable", usedRatio: 0.95),
            UsageWindow(type: .fiveHour, usedRatio: 0.4),
            UsageWindow(type: .weekly, usedRatio: 0.2),
        ])
        XCTAssertEqual(ring.short?.usedRatio, 0.4)
        XCTAssertEqual(ring.long?.usedRatio, 0.2)
    }

    func testFamilyScopedToolUsesItsFirstFamily() {
        let ring = StatusRingSelection.select([
            UsageWindow(type: .fiveHour, providerID: "g5", groupLabel: "Gemini", usedRatio: 0.1),
            UsageWindow(type: .weekly, providerID: "gw", groupLabel: "Gemini", usedRatio: 0.3),
            UsageWindow(type: .fiveHour, providerID: "c5", groupLabel: "Claude/GPT", usedRatio: 0.8),
        ])
        XCTAssertEqual(ring.short?.providerID, "g5")
        XCTAssertEqual(ring.long?.providerID, "gw")
    }

    func testBillingCycleToolUsesItsMostUsedQuota() {
        let ring = StatusRingSelection.select([
            UsageWindow(type: .billingCycle, providerID: "chat", usedRatio: 0.1),
            UsageWindow(type: .billingCycle, providerID: "premium_interactions", usedRatio: 0.65),
        ])
        XCTAssertNil(ring.short)
        XCTAssertEqual(ring.long?.providerID, "premium_interactions")
    }
}

final class SVGPathDataTests: XCTestCase {
    func testReadsAbsoluteAndRelativeCommands() throws {
        let figures = try SVGPathData.parse("M10 10H20V20L10 20Z m5-5 c1 1 2 2 3 3 s4 4 5 5 q1 1 2-.5")
        XCTAssertEqual(figures.count, 2)
        XCTAssertTrue(figures[0].closed)
        XCTAssertEqual(figures[0].segments.count, 3)
        XCTAssertEqual(figures[1].start, SVGFigure.Point(15, 5))
        XCTAssertEqual(figures[1].segments[1].end, SVGFigure.Point(23, 13))
        XCTAssertEqual(figures[1].segments[2].end, SVGFigure.Point(25, 12.5))
    }

    func testRejectsMalformedData() {
        for data in ["10 10", "M0 0 A5 5 0 0 1 10 10", "M0 0 Z 5 5", "M0 0 L5"] {
            XCTAssertThrowsError(try SVGPathData.parse(data), data)
        }
    }

    func testReadsALogoDocument() {
        let logo = SVGLogo(svg: #"<svg viewBox="0 0 24 12"><path d="M0 0H24V12Z" fill-rule="evenodd"/></svg>"#)
        XCTAssertEqual(logo?.width, 24)
        XCTAssertEqual(logo?.height, 12)
        XCTAssertEqual(logo?.evenOdd, true)
        XCTAssertNil(SVGLogo(svg: "<svg/>"))
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
