import Foundation
import XCTest
@testable import GaugeCore

/// Counts fetches and returns a live snapshot with one window.
final class CountingProvider: UsageProvider, @unchecked Sendable {
    let tool: ToolKind
    private let lock = NSLock()
    private var count = 0

    init(_ tool: ToolKind) { self.tool = tool }

    var fetches: Int { lock.withLock { count } }

    func fetch(_ interaction: FetchInteraction) async throws -> ProviderOutcome {
        lock.withLock { count += 1 }
        let window = UsageWindow(type: .fiveHour, usedRatio: 0.2)
        return ProviderOutcome(snapshot: UsageSnapshot(tool: tool, plan: nil, windows: [window], capturedAt: Date()),
                               signedIn: true, isLive: true)
    }
}

final class CoordinatorCadenceTests: XCTestCase {
    func testPopoverOpenRespectsAntigravityCostFloorButManualDoesNot() async {
        let clock = ManualClock()
        let antigravity = CountingProvider(.antigravity)
        let codex = CountingProvider(.codex)
        let coordinator = UsageCoordinator(providers: [antigravity, codex], cache: nil, clock: clock)
        await coordinator.setEnabledTools([.codex, .antigravity])

        await coordinator.refresh(.popoverOpened)
        XCTAssertEqual(antigravity.fetches, 1)
        XCTAssertEqual(codex.fetches, 1)

        // Past the 10 s debounce but inside Antigravity's 2-minute floor.
        clock.advance(30)
        await coordinator.refresh(.popoverOpened)
        XCTAssertEqual(antigravity.fetches, 1, "floor holds back the expensive read")
        XCTAssertEqual(codex.fetches, 2, "one-GET providers stay current on every open")

        await coordinator.refresh(.manual)
        XCTAssertEqual(antigravity.fetches, 2, "an explicit refresh bypasses the floor")

        clock.advance(121)
        await coordinator.refresh(.popoverOpened)
        XCTAssertEqual(antigravity.fetches, 3)
    }

    func testScheduledCadenceStretchesWhileNobodyLooks() async {
        let clock = ManualClock()
        let codex = CountingProvider(.codex)
        let coordinator = UsageCoordinator(providers: [codex], cache: nil, clock: clock)
        await coordinator.setEnabledTools([.codex])

        await coordinator.refresh(.popoverOpened)
        XCTAssertEqual(codex.fetches, 1)
        // Recent interaction: the 3-minute base cadence applies.
        clock.advance(181)
        await coordinator.refresh(.scheduled)
        XCTAssertEqual(codex.fetches, 2)

        // Past five minutes since the open, the warm multiplier (×2) makes it 6 minutes.
        clock.advance(420)
        await coordinator.refresh(.scheduled)
        XCTAssertEqual(codex.fetches, 3, "7 min since the last read ≥ 6 min warm interval")
        clock.advance(200)
        await coordinator.refresh(.scheduled)
        XCTAssertEqual(codex.fetches, 3, "200 s < 6 min warm interval")

        await coordinator.setSignals(AdaptiveRefreshSignals(lowPowerMode: true))
        clock.advance(400)
        await coordinator.refresh(.scheduled)
        XCTAssertEqual(codex.fetches, 3, "Low Power Mode stretches to 30 min")
        clock.advance(1200)
        await coordinator.refresh(.scheduled)
        XCTAssertEqual(codex.fetches, 4)
    }

    func testAdaptivePolicyNeverShortensTheBase() {
        let decision = AdaptiveRefreshPolicy.evaluate(sincePanelOpened: nil, signals: .init())
        XCTAssertEqual(decision.reason, .longIdle)
        XCTAssertEqual(decision.interval(for: 180), 1800)
        XCTAssertEqual(decision.interval(for: 3600), 3600)
        XCTAssertEqual(AdaptiveRefreshPolicy.evaluate(sincePanelOpened: 60, signals: .init()).interval(for: 300), 300)
        XCTAssertEqual(AdaptiveRefreshPolicy.evaluate(sincePanelOpened: 2 * 3600, signals: .init()).interval(for: 300), 1200)
    }

    func testRefreshStartTimeIsClearedWhenTheReadCompletes() async {
        let coordinator = UsageCoordinator(providers: [CountingProvider(.codex)], cache: nil, clock: ManualClock())
        await coordinator.setEnabledTools([.codex])
        let recorder = StatesRecorder()
        await coordinator.setListener { recorder.append($0) }
        await coordinator.refresh(.manual)
        let started = recorder.all.first { $0.first?.isRefreshing == true }
        XCTAssertNotNil(started?.first?.refreshStartedAt)
        XCTAssertNil(recorder.all.last?.first?.refreshStartedAt)
    }
}

final class StatesRecorder: @unchecked Sendable {
    private let lock = NSLock()
    private var states: [[ToolState]] = []
    func append(_ value: [ToolState]) { lock.withLock { states.append(value) } }
    var all: [[ToolState]] { lock.withLock { states } }
}

final class ProviderStatusTests: XCTestCase {
    func testParsesIndicatorsAndRejectsUnknownOnes() throws {
        let json = #"{"status":{"indicator":"minor","description":"Partially\n  Degraded Service"}}"#
        let status = try ProviderStatusService.parse(.claude, data: Data(json.utf8), checkedAt: Date())
        XCTAssertEqual(status.level, .minor)
        XCTAssertEqual(status.summary, "Partially Degraded Service")
        XCTAssertTrue(status.isVisible)
        XCTAssertThrowsError(try ProviderStatusService.parse(.claude, data: Data(#"{"status":{"indicator":"weird"}}"#.utf8), checkedAt: Date()))
        let ok = try ProviderStatusService.parse(.codex, data: Data(#"{"status":{"indicator":"none"}}"#.utf8), checkedAt: Date())
        XCTAssertFalse(ok.isVisible)
    }

    func testFailedCheckKeepsLastObservationAsUnconfirmed() async {
        let transport = FakeTransport()
        transport.enqueue(.status(200, #"{"status":{"indicator":"major","description":"Outage"}}"#))
        let clock = ManualClock()
        let service = ProviderStatusService(transport: transport, clock: clock)
        await service.setTools([.claude])
        clock.advance(16 * 60)
        await service.poll() // FakeTransport fails once its queue is empty
        let recorder = StatusRecorder()
        await service.setListener { recorder.value = $0 }
        await service.setTools([.claude])
        let status = recorder.value[.claude]
        XCTAssertEqual(status?.level, .major)
        XCTAssertEqual(status?.lastCheckFailed, true)
    }
}

final class StatusRecorder: @unchecked Sendable {
    var value: [ToolKind: ProviderStatus] = [:]
}

final class UpdateServiceTests: XCTestCase {
    func testVersionsCompareNumerically() {
        XCTAssertLessThan(AppVersion("v0.9.0")!, AppVersion("0.10.0")!)
        XCTAssertEqual(AppVersion("1.2")!, AppVersion("1.2.0-beta.1")!)
        XCTAssertNil(AppVersion("latest"))
    }

    func testOffersOnlyANewerReleaseWithAVerifiableMacAsset() {
        let digest = String(repeating: "ab", count: 32)
        func release(_ tag: String, name: String = UpdateService.assetName, digest: String? = "sha256:\(digest)") -> Data {
            let digestField = digest.map { #","digest":"\#($0)""# } ?? ""
            return Data(#"{"tag_name":"\#(tag)","assets":[{"name":"\#(name)","browser_download_url":"https://github.com/x/y/releases/download/\#(tag)/\#(name)"\#(digestField)}]}"#.utf8)
        }
        let current = AppVersion("0.5.0")!
        guard case .available(let found) = UpdateService.evaluate(release("v0.6.0"), current: current) else {
            return XCTFail("expected an update")
        }
        XCTAssertEqual(found.sha256, digest)
        XCTAssertEqual(UpdateService.evaluate(release("v0.5.0"), current: current), .upToDate)
        XCTAssertEqual(UpdateService.evaluate(release("v0.6.0", digest: nil), current: current), .failed)
        XCTAssertEqual(UpdateService.evaluate(release("v0.6.0", digest: "md5:abc"), current: current), .failed)
        XCTAssertEqual(UpdateService.evaluate(release("v0.6.0", name: "GaugeSetup-win-x64.exe"), current: current), .failed)
        XCTAssertEqual(UpdateService.evaluate(release("v0.4.0", name: "GaugeSetup-win-x64.exe"), current: current), .upToDate)
    }
}
