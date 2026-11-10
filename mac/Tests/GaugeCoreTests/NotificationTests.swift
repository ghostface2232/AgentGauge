import Foundation
import XCTest
@testable import GaugeCore

final class NotificationEvaluatorTests: XCTestCase {
    private let start = Date(timeIntervalSince1970: 1_000_000)

    private func state(_ ratio: Double, type: UsageWindowType = .fiveHour, reset: Date?, at captured: Date,
                       rehydrated: Bool = false) -> ToolState {
        let window = UsageWindow(type: type, usedRatio: ratio, resetTime: reset, duration: 5 * 3600)
        var state = ToolState(tool: .claude, snapshot: UsageSnapshot(tool: .claude, plan: nil, windows: [window], capturedAt: captured))
        state.isRehydrated = rehydrated
        return state
    }

    func testResetExpiryAlertsOncePerDeadlineFromLiveObservations() {
        var evaluator = UsageNotificationEvaluator()
        let expiry = start + 2 * 86_400
        func observe(_ hour: TimeInterval, _ deadline: Date?, failed: Bool = false, rehydrated: Bool = false) -> [UsageNotification] {
            let captured = start + hour * 3600
            let window = UsageWindow(type: .weekly, usedRatio: 0.6, resetTime: start + 7 * 86_400)
            let snapshot = UsageSnapshot(tool: .codex, plan: nil, windows: [window],
                                         resetCreditDeadlines: deadline.map { [ResetCreditDeadline(expiresAt: $0)] }, capturedAt: captured)
            var state = ToolState(tool: .codex, snapshot: snapshot)
            state.lastRefreshFailed = failed
            state.isRehydrated = rehydrated
            return evaluator.evaluate([state], now: captured)
        }

        XCTAssertTrue(observe(-1, expiry).isEmpty)
        XCTAssertTrue(observe(0, expiry, failed: true).isEmpty)
        XCTAssertTrue(observe(0, expiry, rehydrated: true).isEmpty)
        let alert = observe(0, expiry)
        XCTAssertEqual(alert.map(\.kind), [.resetCredits])
        XCTAssertEqual(alert.map(\.level), [.caution])
        XCTAssertNil(alert.first?.windowID)
        XCTAssertTrue(observe(1, expiry).isEmpty)
        XCTAssertTrue(observe(49, expiry).isEmpty)
        XCTAssertTrue(observe(50, nil).isEmpty)
        XCTAssertEqual(observe(51, start + 55 * 3600).map(\.level), [.danger])
    }

    func testResetExpiryAlertsIndependentlyForAccountsWithTheSameDeadline() {
        var evaluator = UsageNotificationEvaluator()
        let work = ToolAccount(tool: .codex, label: "Work")
        let snapshot = UsageSnapshot(tool: .codex, plan: nil, windows: [],
            resetCreditDeadlines: [ResetCreditDeadline(expiresAt: start + 3600)], capturedAt: start)
        let states = [ToolState(tool: .codex, snapshot: snapshot),
                      ToolState(tool: .codex, snapshot: snapshot, account: work)]
        let alerts = evaluator.evaluate(states, now: start)

        XCTAssertEqual(alerts.map(\.accountKey), [UsageAccountKey(tool: .codex), work.key])
        XCTAssertTrue(alerts.last?.title.contains(work.displayName) == true)
        XCTAssertTrue(evaluator.evaluate(states, now: start).isEmpty)
    }

    func testBaselineNeverAlerts() {
        var evaluator = UsageNotificationEvaluator()
        XCTAssertTrue(evaluator.evaluate([state(0.95, reset: start, at: start)], now: start).isEmpty)
    }

    func testOnlyTheAccountThatCrossedTheThresholdAlerts() {
        let account = ToolAccount(tool: .claude, label: "Work")
        var evaluator = UsageNotificationEvaluator()
        let reset = start.addingTimeInterval(3600)
        let primary = state(0.95, reset: reset, at: start)
        let baseline = state(0.2, reset: reset, at: start)
        let extra = ToolState(tool: .claude, snapshot: baseline.snapshot, account: account)
        XCTAssertTrue(evaluator.evaluate([primary, extra], now: start).isEmpty)

        let captured = start + 60
        let crossing = state(0.92, reset: reset, at: captured)
        let updated = ToolState(tool: .claude, snapshot: crossing.snapshot, account: account)
        let alerts = evaluator.evaluate([primary, updated], now: captured)
        XCTAssertEqual(alerts.map(\.accountKey), [account.key])
        XCTAssertTrue(alerts.first?.title.contains("Work") == true)
    }

    func testNewLoginInTheSameProfileStartsAFreshAlertBaseline() {
        var evaluator = UsageNotificationEvaluator()
        let account = ToolAccount(tool: .claude, label: "Work")
        let reset = start + 3600
        func reading(_ ratio: Double, at time: Date, identity: String) -> ToolState {
            var snapshot = state(ratio, reset: reset, at: time).snapshot
            snapshot?.historyIdentity = identity
            return ToolState(tool: .claude, snapshot: snapshot, account: account)
        }
        _ = evaluator.evaluate([reading(0.2, at: start, identity: "alice")], now: start)
        XCTAssertTrue(evaluator.evaluate([reading(0.95, at: start + 60, identity: "bob")], now: start + 60).isEmpty)
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

    private let hour: TimeInterval = 3600
    private let day: TimeInterval = 86_400

    // Claude reports an idle window with a null reset time, so a used-up window comes back as
    // 0% with none and only gets its time again on first use.
    func testResetIntoAWindowWithNoResetTimeStillCounts() {
        var evaluator = UsageNotificationEvaluator()
        _ = evaluator.evaluate([state(0.95, reset: start + hour, at: start)], now: start)
        let alerts = evaluator.evaluate([state(0, reset: nil, at: start + 2 * hour)], now: start)
        XCTAssertEqual(alerts.map(\.kind), [.reset])
    }

    func testThresholdsAreRearmedAfterAResetThroughNoResetTime() {
        var evaluator = UsageNotificationEvaluator()
        _ = evaluator.evaluate([state(0.85, reset: start + hour, at: start)], now: start)
        XCTAssertEqual(evaluator.evaluate([state(0.95, reset: start + hour, at: start + 300)], now: start).map(\.kind), [.threshold])

        _ = evaluator.evaluate([state(0, reset: nil, at: start + 2 * hour)], now: start)
        XCTAssertTrue(evaluator.evaluate([state(0.03, reset: start + 7 * hour, at: start + 2.5 * hour)], now: start).isEmpty)

        let crossing = evaluator.evaluate([state(0.92, reset: start + 7 * hour, at: start + 4 * hour)], now: start)
        XCTAssertEqual(crossing.map(\.kind), [.threshold])
    }

    func testOldHighWaterMarkCannotFakeAResetToastInALaterCycle() {
        var evaluator = UsageNotificationEvaluator()
        _ = evaluator.evaluate([state(0.95, reset: start + hour, at: start)], now: start)
        _ = evaluator.evaluate([state(0, reset: nil, at: start + 2 * hour)], now: start)
        _ = evaluator.evaluate([state(0.25, reset: start + 7 * hour, at: start + 3 * hour)], now: start)

        // This cycle never came near the threshold, so its reset is not worth announcing.
        let next = evaluator.evaluate([state(0.03, reset: start + 13 * hour, at: start + 8 * hour)], now: start)
        XCTAssertTrue(next.isEmpty)
    }

    func testStrongDropWithTheResetTimeReturningAlsoCounts() {
        var evaluator = UsageNotificationEvaluator()
        _ = evaluator.evaluate([state(0.95, reset: nil, at: start)], now: start)
        let alerts = evaluator.evaluate([state(0.02, reset: start + 5 * hour, at: start + hour)], now: start)
        XCTAssertEqual(alerts.map(\.kind), [.reset])
    }

    func testMissingResetTimeWithoutAStrongDropIsNotAReset() {
        var evaluator = UsageNotificationEvaluator()
        _ = evaluator.evaluate([state(0.95, reset: start + hour, at: start)], now: start)

        // Schema drift dropping the field must not read as a new cycle on its own.
        XCTAssertTrue(evaluator.evaluate([state(0.90, reset: nil, at: start + 300)], now: start).isEmpty)
        XCTAssertTrue(evaluator.evaluate([state(0.95, reset: start + hour, at: start + 600)], now: start).isEmpty)
    }

    func testCycleThatAdvancedWithoutADropRearmsWithoutAToast() {
        var evaluator = UsageNotificationEvaluator()
        _ = evaluator.evaluate([state(0.60, type: .weekly, reset: start + day, at: start)], now: start)
        XCTAssertEqual(evaluator.evaluate([state(0.75, type: .weekly, reset: start + day, at: start + hour)], now: start).map(\.kind), [.threshold])

        // A new cycle that by the next poll is back near the old reading: 75% to 66% is under
        // the drop that would make this a reset, and no toast is due for it.
        XCTAssertTrue(evaluator.evaluate([state(0.66, type: .weekly, reset: start + 8 * day, at: start + 2 * day)], now: start).isEmpty)

        let crossing = evaluator.evaluate([state(0.72, type: .weekly, reset: start + 8 * day, at: start + 3 * day)], now: start)
        XCTAssertEqual(crossing.map(\.kind), [.threshold])
    }

    func testCycleAlreadyAboveAThresholdIsMarkedConsumed() {
        var evaluator = UsageNotificationEvaluator()
        _ = evaluator.evaluate([state(0.95, type: .weekly, reset: start + day, at: start)], now: start)

        // Already at 92% when first seen in its new cycle, with no drop that makes it a reset.
        XCTAssertTrue(evaluator.evaluate([state(0.92, type: .weekly, reset: start + 8 * day, at: start + 2 * day)], now: start).isEmpty)
        XCTAssertTrue(evaluator.evaluate([state(0.96, type: .weekly, reset: start + 8 * day, at: start + 3 * day)], now: start).isEmpty)
    }

    func testResetTimeJitterDoesNotRearmAThresholdAlreadyAnnounced() {
        var evaluator = UsageNotificationEvaluator()
        _ = evaluator.evaluate([state(0.60, type: .weekly, reset: start + day, at: start)], now: start)
        XCTAssertEqual(evaluator.evaluate([state(0.75, type: .weekly, reset: start + day, at: start + hour)], now: start).map(\.kind), [.threshold])

        let jittered = start + day + 1800
        XCTAssertTrue(evaluator.evaluate([state(0.66, type: .weekly, reset: jittered, at: start + 2 * hour)], now: start).isEmpty)
        XCTAssertTrue(evaluator.evaluate([state(0.72, type: .weekly, reset: jittered, at: start + 3 * hour)], now: start).isEmpty)
    }

    func testSnapshotRestoredFromDiskIsNotABaseline() {
        // Off since Monday at 65%. Comparing Wednesday's first live 75% with it would announce
        // a crossing that happened while the app was not running.
        var evaluator = UsageNotificationEvaluator()
        let reset = start + 4 * day
        XCTAssertTrue(evaluator.evaluate([state(0.65, type: .weekly, reset: reset, at: start, rehydrated: true)], now: start).isEmpty)

        let firstLive = evaluator.evaluate([state(0.75, type: .weekly, reset: reset, at: start + 2 * day)], now: start)
        let nextCrossing = evaluator.evaluate([state(0.92, type: .weekly, reset: reset, at: start + 2 * day + hour)], now: start)

        XCTAssertTrue(firstLive.isEmpty)
        XCTAssertEqual(nextCrossing.map(\.level), [.danger])
    }

    func testResetWhileTheAppWasOffIsNotAnnounced() {
        var evaluator = UsageNotificationEvaluator()
        _ = evaluator.evaluate([state(0.95, reset: start + hour, at: start, rehydrated: true)], now: start)
        XCTAssertTrue(evaluator.evaluate([state(0.02, reset: start + 25 * hour, at: start + day)], now: start).isEmpty)
    }

    func testFailedRefreshOfARestoredSnapshotIsStillNotABaseline() {
        var evaluator = UsageNotificationEvaluator()
        let reset = start + 4 * day
        var failed = state(0.65, type: .weekly, reset: reset, at: start, rehydrated: true)
        failed.lastRefreshFailed = true
        XCTAssertTrue(evaluator.evaluate([failed], now: start).isEmpty)
        XCTAssertTrue(evaluator.evaluate([state(0.75, type: .weekly, reset: reset, at: start + day)], now: start).isEmpty)
    }

    /// A tool hidden since launch and shown now: its restored snapshot takes the place of the
    /// stale cache the resume mark expects, so only the first live reading is a baseline.
    func testShowingAToolStillOnARestoredSnapshotSwallowsNoLiveReading() {
        var evaluator = UsageNotificationEvaluator()
        let reset = start + 4 * day
        evaluator.toolShown(.claude)
        XCTAssertTrue(evaluator.evaluate([state(0.65, type: .weekly, reset: reset, at: start, rehydrated: true)], now: start).isEmpty)

        XCTAssertTrue(evaluator.evaluate([state(0.75, type: .weekly, reset: reset, at: start + day)], now: start).isEmpty)
        let crossing = evaluator.evaluate([state(0.92, type: .weekly, reset: reset, at: start + day + hour)], now: start)
        XCTAssertEqual(crossing.map(\.level), [.danger])
    }
}

final class MenuBarSummaryTests: XCTestCase {
    func testHighestWindowDrivesLevel() {
        let snapshot = UsageSnapshot(tool: .codex, plan: nil, windows: [
            UsageWindow(type: .fiveHour, usedRatio: 0.2),
            UsageWindow(type: .weekly, usedRatio: 0.75),
        ], capturedAt: Date())
        // Without a target the icon follows the most-used tool, so the lone tool's ring
        // reads both arcs; its level follows the 5-hour arc.
        let summary = MenuBarSummary(states: [ToolState(tool: .codex, snapshot: snapshot)])
        XCTAssertEqual(summary.ring?.tool, .codex)
        XCTAssertEqual(summary.level, .ok)
        XCTAssertEqual(summary.percentText(basis: .used), "5H 20% | W 75%")
        XCTAssertEqual(summary.percentText(basis: .remaining), "5H 80% | W 25%")
        XCTAssertNil(MenuBarSummary(states: []).level)
        XCTAssertNil(MenuBarSummary(states: []).ring)

        // A tool with no window the ring can draw leaves the classic gauge and its level.
        let scoped = UsageSnapshot(tool: .codex, plan: nil, windows: [], capturedAt: Date())
        XCTAssertNil(MenuBarSummary(states: [ToolState(tool: .codex, snapshot: scoped)]).ring)
    }

    func testManualAndAutomaticServiceSelection() {
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

        // The ring carries both arcs, and the text beside it labels both.
        let codex = MenuBarSummary(states: states, selectedAccounts: [UsageAccountKey(tool: .codex)])
        XCTAssertEqual(codex.selectedRings?.first?.tool, .codex)
        XCTAssertEqual(codex.selectedRings?.first?.short?.usedRatio, 0.2)
        XCTAssertEqual(codex.selectedRings?.first?.long?.usedRatio, 0.75)
        XCTAssertEqual(codex.percentText(basis: .used), "5H 20% | W 75%")
        XCTAssertEqual(codex.percentText(basis: .remaining), "5H 80% | W 25%")

        // A tool with only a billing cycle shows that arc alone.
        let cursor = MenuBarSummary(states: [ToolState(tool: .cursor, snapshot: UsageSnapshot(
            tool: .cursor, plan: nil, windows: [UsageWindow(type: .billingCycle, usedRatio: 0.4)], capturedAt: Date()))],
            selectedAccounts: [UsageAccountKey(tool: .cursor)])
        XCTAssertEqual(cursor.percentText(basis: .used), "M 40%")

        // An explicit selection stays in place even if the service has no reading.
        let missing = MenuBarSummary(states: states, selectedAccounts: [UsageAccountKey(tool: .cursor)])
        XCTAssertEqual(missing.selectedRings?.first?.tool, .cursor)
        XCTAssertNil(missing.percentText(basis: .used))
        let auto = MenuBarSummary(states: states)
        XCTAssertNil(auto.selectedRings)
        XCTAssertEqual(auto.ring?.tool, .claude)
        XCTAssertEqual(auto.level, .danger)

        // Equal tools keep the first, so the icon does not flip between them.
        let tied = [
            ToolState(tool: .codex, snapshot: UsageSnapshot(tool: .codex, plan: nil,
                windows: [UsageWindow(type: .fiveHour, usedRatio: 0.5)], capturedAt: Date())),
            ToolState(tool: .claude, snapshot: UsageSnapshot(tool: .claude, plan: nil,
                windows: [UsageWindow(type: .fiveHour, usedRatio: 0.5)], capturedAt: Date())),
        ]
        XCTAssertEqual(MenuBarSummary(states: tied).ring?.tool, .codex)

        // The hover text lists each tool's two arcs on its own line.
        XCTAssertEqual(auto.tooltip.split(separator: "\n").count, 2)
        XCTAssertTrue(auto.tooltip.contains("20%"))
    }

    func testSelectingAServiceLeavesAutoAndSavedSelectionsOverrideLegacyTargets() {
        var selection = MenuBarSelection(savedValues: nil, legacyTarget: "")
        XCTAssertEqual(selection, .automatic)

        let claude = UsageAccountKey(tool: .claude)
        let codex = UsageAccountKey(tool: .codex)
        selection.set(claude, selected: true)
        selection.set(codex, selected: true)
        XCTAssertEqual(selection, .accounts([claude, codex]))
        XCTAssertEqual(MenuBarSelection(savedValues: selection.savedValues), selection)
        selection = .automatic
        XCTAssertNil(selection.selectedAccounts)
        XCTAssertEqual(MenuBarSelection(savedValues: selection.savedValues, legacyTarget: "codex"), .automatic)

        XCTAssertEqual(MenuBarSelection(savedValues: nil, legacyTarget: "codex:weekly"), .accounts([codex]))
        XCTAssertEqual(MenuBarSelection(savedValues: nil, legacyTarget: "Claude"), .accounts([claude]))
        XCTAssertEqual(MenuBarSelection(savedValues: [], legacyTarget: "Claude"), .accounts([]))
    }

    func testMenuBarSelectionKeepsTwoAccountsOfTheSameServiceAndMigratesAnAccountTarget() {
        let work = ToolAccount(tool: .codex, label: "Work")
        let primary = UsageAccountKey(tool: .codex)
        var selection = MenuBarSelection(savedValues: nil, legacyTarget: work.key.storageKey)
        XCTAssertEqual(selection.selectedAccounts, [work.key])
        selection.set(primary, selected: true)
        XCTAssertEqual(MenuBarSelection(savedValues: selection.savedValues), selection)

        let snapshot = UsageSnapshot(tool: .codex, plan: nil, windows: [UsageWindow(type: .fiveHour, usedRatio: 0.2)],
                                     capturedAt: Date())
        let states = [ToolState(tool: .codex, snapshot: snapshot), ToolState(tool: .codex, account: work)]
        let summary = MenuBarSummary(states: states, selectedAccounts: selection.selectedAccounts)
        XCTAssertEqual(summary.selectedRings?.map(\.accountKey), [primary, work.key])
        XCTAssertNil(summary.selectedRings?.last?.short)
        XCTAssertTrue(summary.tooltip.contains("Codex · Work"))
    }

    func testMultipleRingsFollowSelectionInServiceOrderWithoutPercentText() {
        let states = [
            ToolState(tool: .codex, snapshot: UsageSnapshot(tool: .codex, plan: nil,
                windows: [UsageWindow(type: .fiveHour, usedRatio: 0.95)], capturedAt: Date())),
            ToolState(tool: .claude, snapshot: UsageSnapshot(tool: .claude, plan: nil,
                windows: [UsageWindow(type: .weekly, usedRatio: 0.25)], capturedAt: Date())),
        ]
        let summary = MenuBarSummary(states: states,
            selectedAccounts: [UsageAccountKey(tool: .cursor), UsageAccountKey(tool: .claude)], displayBasis: .remaining)

        XCTAssertEqual(summary.selectedRings?.map(\.tool), [.claude, .cursor])
        XCTAssertEqual(summary.selectedRings?.first?.long?.usedRatio, 0.25)
        XCTAssertNil(summary.selectedRings?.last?.long)
        XCTAssertNil(summary.percentText(basis: .remaining))
        XCTAssertTrue(summary.tooltip.contains("75%"))
        XCTAssertTrue(summary.tooltip.contains("Cursor  \(L10n.get("NoData"))"))
        XCTAssertFalse(summary.tooltip.contains("Codex"))

        let empty = MenuBarSummary(states: states, selectedAccounts: [])
        XCTAssertEqual(empty.selectedRings, [])
        XCTAssertNil(empty.ring)
        XCTAssertNil(empty.percentText(basis: .used))
        XCTAssertTrue(empty.tooltip.isEmpty)
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
