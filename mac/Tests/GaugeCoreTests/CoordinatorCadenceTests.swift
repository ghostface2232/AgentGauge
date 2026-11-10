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

/// Parks its first fetch until released and ignores cancellation meanwhile, the way a stuck
/// blocking call does. Later fetches return at once.
final class HangingProvider: UsageProvider, @unchecked Sendable {
    let tool: ToolKind
    private let lock = NSLock()
    private var count = 0
    private var parked: CheckedContinuation<Void, Never>?
    private var released = false
    private var cancelled = false

    init(_ tool: ToolKind) { self.tool = tool }

    var fetches: Int { lock.withLock { count } }
    var sawCancellation: Bool { lock.withLock { cancelled } }

    func fetch(_ interaction: FetchInteraction) async throws -> ProviderOutcome {
        let isFirst = lock.withLock { () -> Bool in
            count += 1
            return count == 1
        }
        if isFirst {
            await withCheckedContinuation { (continuation: CheckedContinuation<Void, Never>) in
                let resumeNow = lock.withLock { () -> Bool in
                    if released { return true }
                    parked = continuation
                    return false
                }
                if resumeNow { continuation.resume() }
            }
            let isCancelled = Task.isCancelled
            lock.withLock { cancelled = isCancelled }
        }
        let window = UsageWindow(type: .fiveHour, usedRatio: 0.2)
        return ProviderOutcome(snapshot: UsageSnapshot(tool: tool, plan: nil, windows: [window], capturedAt: Date()),
                               signedIn: true, isLive: true)
    }

    func release() {
        let waiter = lock.withLock { () -> CheckedContinuation<Void, Never>? in
            released = true
            defer { parked = nil }
            return parked
        }
        waiter?.resume()
    }
}

/// Never answers, but honours cancellation the way a URLSession read does.
final class SleepingProvider: UsageProvider, @unchecked Sendable {
    let tool: ToolKind
    private let lock = NSLock()
    private var count = 0

    init(_ tool: ToolKind) { self.tool = tool }

    var fetches: Int { lock.withLock { count } }

    func fetch(_ interaction: FetchInteraction) async throws -> ProviderOutcome {
        lock.withLock { count += 1 }
        try await Task.sleep(for: .seconds(3600))
        throw UsageFetchError.invalidResponse
    }
}

final class CoordinatorDeadlineTests: XCTestCase {
    private func waitUntil(_ condition: () -> Bool) async {
        for _ in 0..<500 where !condition() {
            try? await Task.sleep(for: .milliseconds(10))
        }
    }

    func testHungProviderHoldsBackNeitherOtherToolsNorTheScheduler() async {
        let clock = ManualClock()
        let hung = HangingProvider(.claude)
        let codex = CountingProvider(.codex)
        let coordinator = UsageCoordinator(providers: [hung, codex], cache: nil, clock: clock, fetchDeadline: .milliseconds(100))
        await coordinator.setEnabledTools([.claude, .codex])
        let recorder = StatesRecorder()
        await coordinator.setListener { recorder.append($0) }
        let claudeState = { recorder.all.last?.first { $0.tool == .claude } }

        await coordinator.refresh(.popoverOpened)
        XCTAssertEqual(codex.fetches, 1)
        XCTAssertEqual(claudeState()?.lastRefreshFailed, true, "a read past its deadline counts as a failure")
        XCTAssertEqual(claudeState()?.isRefreshing, false)

        clock.advance(181)
        await coordinator.refresh(.scheduled)
        XCTAssertEqual(codex.fetches, 2, "the next tick still refreshes the healthy tool")

        clock.advance(11)
        await coordinator.refresh(.manual)
        XCTAssertEqual(codex.fetches, 3)
        XCTAssertEqual(hung.fetches, 1, "no second fetch may start beside one that has not ended")

        hung.release()
        await waitUntil { claudeState()?.lastRefreshFailed == false }
        XCTAssertEqual(claudeState()?.lastRefreshFailed, false, "a late success still lands")
        XCTAssertNotNil(claudeState()?.snapshot)
        XCTAssertTrue(hung.sawCancellation, "the deadline cancels the read")

        clock.advance(11)
        await coordinator.refresh(.manual)
        XCTAssertEqual(hung.fetches, 2, "the tool is free again once its read has really ended")
    }

    func testCancellableReadIsFreedAfterItsDeadline() async {
        let clock = ManualClock()
        let sleeping = SleepingProvider(.codex)
        let coordinator = UsageCoordinator(providers: [sleeping], cache: nil, clock: clock, fetchDeadline: .milliseconds(50))
        await coordinator.setEnabledTools([.codex])
        let recorder = StatesRecorder()
        await coordinator.setListener { recorder.append($0) }

        await coordinator.refresh(.manual)
        XCTAssertEqual(recorder.all.last?.first?.lastRefreshFailed, true)

        // The cancelled read ends on its own task, so the tool may still be in flight at first.
        for _ in 0..<200 where sleeping.fetches < 2 {
            clock.advance(11)
            await coordinator.refresh(.manual)
            try? await Task.sleep(for: .milliseconds(10))
        }
        XCTAssertEqual(sleeping.fetches, 2)
        XCTAssertEqual(recorder.all.last?.first?.lastRefreshFailed, true, "the cancellation itself changes nothing")
    }

    func testUnreadableCredentialKeepsTheSnapshotAndTheDiskCache() async throws {
        let directory = FileManager.default.temporaryDirectory.appending(path: UUID().uuidString, directoryHint: .isDirectory)
        defer { try? FileManager.default.removeItem(at: directory) }
        let cache = UsageCacheStore(directory: directory)
        let clock = ManualClock()
        let transport = FakeTransport()
        transport.enqueue(.status(200, #"{"five_hour":{"utilization":40,"resets_at":"2026-10-19T05:00:00Z"}}"#))
        let credentials = FakeCredentials(.available(ToolCredential(accessToken: "a")))
        let provider = HTTPUsageProvider(tool: .claude, credentials: credentials, endpoint: ClaudeEndpoint(),
                                         policy: .claude, transport: transport, clock: clock)
        let coordinator = UsageCoordinator(providers: [provider], cache: cache, clock: clock)
        await coordinator.setEnabledTools([.claude])
        let recorder = StatesRecorder()
        await coordinator.setListener { recorder.append($0) }

        await coordinator.refresh(.manual)
        XCTAssertEqual(cache.load().count, 1)

        credentials.set(.invalid(.unreadable))
        clock.advance(11)
        await coordinator.refresh(.manual)
        let state = try XCTUnwrap(recorder.all.last?.first)
        XCTAssertEqual(state.auth, .unreadable)
        XCTAssertTrue(state.lastRefreshFailed)
        XCTAssertEqual(state.snapshot?.windows.count, 1)
        XCTAssertEqual(cache.load().count, 1)
    }
}

final class CoordinatorRehydrationTests: XCTestCase {
    func testNamedCopilotAccountsReplaceTheActiveFallback() async throws {
        let primary = CountingProvider(.githubCopilot)
        let secondary = CountingProvider(.githubCopilot)
        let coordinator = UsageCoordinator(providers: [primary], cache: nil)
        let recorder = StatesRecorder()
        await coordinator.setListener { recorder.append($0) }
        await coordinator.setEnabledTools([.githubCopilot])
        await coordinator.refresh(.launch)
        let label = try XCTUnwrap(ToolAccount.validatedLabel("mona-cat_octo", for: .githubCopilot))
        let account = ToolAccount(tool: .githubCopilot, label: label)
        await coordinator.addAccount(AccountUsageProvider(account: account, provider: secondary))
        await coordinator.refresh(.toolsChanged, account: account.key)
        XCTAssertEqual(primary.fetches, 1)
        XCTAssertEqual(secondary.fetches, 1)
        XCTAssertEqual(recorder.all.last?.map(\.id), [account.key])

        await coordinator.removeAccount(account.key)
        let fallback = UsageAccountKey(tool: .githubCopilot)
        await coordinator.refresh(.toolsChanged, account: fallback)
        XCTAssertEqual(primary.fetches, 2)
        XCTAssertEqual(recorder.all.last?.map(\.id), [fallback])
    }

    func testAdditionalAccountHasSeparateCacheAndCanBeRemovedIndependently() async throws {
        let directory = FileManager.default.temporaryDirectory.appending(path: UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: directory) }
        let cache = UsageCacheStore(directory: directory)
        let account = ToolAccount(tool: .codex, label: "Work")
        let primary = CountingProvider(.codex)
        let secondary = CountingProvider(.codex)
        let extra = AccountUsageProvider(account: account, provider: secondary)
        let coordinator = UsageCoordinator(providers: [primary], cache: cache, additionalProviders: [extra])
        await coordinator.setEnabledTools([.codex])
        await coordinator.refresh(.launch)
        XCTAssertEqual(primary.fetches, 1)
        XCTAssertEqual(secondary.fetches, 1)
        XCTAssertEqual(Set(cache.load().map(\.accountKey)), [UsageAccountKey(tool: .codex), account.key])

        let restarted = UsageCoordinator(providers: [primary], cache: cache, additionalProviders: [extra])
        await restarted.setEnabledTools([.codex])
        let recorder = StatesRecorder()
        await restarted.setListener { recorder.append($0) }
        let rehydrated = try XCTUnwrap(recorder.all.last)
        XCTAssertEqual(rehydrated.map(\.displayName), ["Codex", "Codex · Work"])
        XCTAssertTrue(rehydrated.allSatisfy(\.isRehydrated))

        await restarted.refresh(.toolsChanged, account: account.key)
        XCTAssertEqual(primary.fetches, 1)
        XCTAssertEqual(secondary.fetches, 2)
        await restarted.removeAccount(account.key)
        XCTAssertEqual(cache.load().map(\.accountKey), [UsageAccountKey(tool: .codex)])
        XCTAssertEqual(recorder.all.last?.map(\.displayName), ["Codex"])
    }

    func testRestoredSnapshotStaysMarkedUntilTheFirstSuccessfulRead() async throws {
        let directory = FileManager.default.temporaryDirectory.appending(path: UUID().uuidString, directoryHint: .isDirectory)
        defer { try? FileManager.default.removeItem(at: directory) }
        let cache = UsageCacheStore(directory: directory)
        cache.save([UsageSnapshot(tool: .claude, plan: nil, windows: [UsageWindow(type: .fiveHour, usedRatio: 0.4)],
                                  capturedAt: Date(timeIntervalSince1970: 1_800_000_000))])

        let clock = ManualClock()
        let transport = FakeTransport() // Fails while its queue is empty.
        let provider = HTTPUsageProvider(tool: .claude, credentials: FakeCredentials(.available(ToolCredential(accessToken: "a"))),
                                         endpoint: ClaudeEndpoint(), policy: FetchPolicy(), transport: transport, clock: clock)
        let coordinator = UsageCoordinator(providers: [provider], cache: cache, clock: clock)
        await coordinator.setEnabledTools([.claude])
        let recorder = StatesRecorder()
        await coordinator.setListener { recorder.append($0) }
        XCTAssertEqual(recorder.all.last?.first?.isRehydrated, true)

        await coordinator.refresh(.manual)
        XCTAssertEqual(recorder.all.last?.first?.lastRefreshFailed, true)
        XCTAssertEqual(recorder.all.last?.first?.isRehydrated, true, "a failed refresh still shows the restored snapshot")

        transport.enqueue(.status(200, #"{"five_hour":{"utilization":40,"resets_at":"2026-10-19T05:00:00Z"}}"#))
        clock.advance(11)
        await coordinator.refresh(.manual)
        XCTAssertEqual(recorder.all.last?.first?.lastRefreshFailed, false)
        XCTAssertEqual(recorder.all.last?.first?.isRehydrated, false)
    }
}

final class CoordinatorDebounceTests: XCTestCase {
    func testManualRefreshIsDebouncedSoClicksCannotKeepPiercingACooldown() async {
        let clock = ManualClock()
        let codex = CountingProvider(.codex)
        let coordinator = UsageCoordinator(providers: [codex], cache: nil, clock: clock)
        await coordinator.setEnabledTools([.codex])

        await coordinator.refresh(.manual)
        XCTAssertEqual(codex.fetches, 1)
        clock.advance(9)
        await coordinator.refresh(.manual)
        XCTAssertEqual(codex.fetches, 1, "a second click inside 10 s shows the cached state")
        clock.advance(2)
        await coordinator.refresh(.manual)
        XCTAssertEqual(codex.fetches, 2)
    }

    func testAnyRefreshThatFetchesArmsTheDebounce() async {
        let arming: [RefreshReason] = [.launch, .scheduled, .toolsChanged, .popoverOpened, .systemResumed, .manual]
        for reason in arming {
            for debounced in [RefreshReason.popoverOpened, .manual, .systemResumed] {
                let clock = ManualClock()
                let codex = CountingProvider(.codex)
                let coordinator = UsageCoordinator(providers: [codex], cache: nil, clock: clock)
                await coordinator.setEnabledTools([.codex])

                await coordinator.refresh(reason)
                XCTAssertEqual(codex.fetches, 1)
                clock.advance(1)
                await coordinator.refresh(debounced)
                XCTAssertEqual(codex.fetches, 1, "\(reason) should hold back \(debounced)")
            }
        }
    }

    func testRefreshThatFetchesNothingDoesNotArmTheDebounce() async {
        let clock = ManualClock()
        let antigravity = CountingProvider(.antigravity)
        let floored = UsageCoordinator(providers: [antigravity], cache: nil, clock: clock)
        await floored.setEnabledTools([.antigravity])
        await floored.refresh(.popoverOpened)
        clock.advance(30)
        await floored.refresh(.popoverOpened)
        XCTAssertEqual(antigravity.fetches, 1, "inside the cost floor")
        await floored.refresh(.manual)
        XCTAssertEqual(antigravity.fetches, 2, "the floored open fetched nothing, so nothing holds this back")

        let codex = CountingProvider(.codex)
        let idleTick = UsageCoordinator(providers: [codex], cache: nil, clock: clock)
        await idleTick.setEnabledTools([.codex])
        await idleTick.refresh(.popoverOpened)
        clock.advance(30)
        await idleTick.refresh(.scheduled)
        XCTAssertEqual(codex.fetches, 1, "not due yet")
        await idleTick.refresh(.manual)
        XCTAssertEqual(codex.fetches, 2)
    }

    func testToolChangeIsNotDebounced() async {
        let clock = ManualClock()
        let codex = CountingProvider(.codex)
        let coordinator = UsageCoordinator(providers: [codex], cache: nil, clock: clock)
        await coordinator.setEnabledTools([.codex])
        await coordinator.refresh(.manual)
        clock.advance(1)
        await coordinator.refresh(.toolsChanged)
        XCTAssertEqual(codex.fetches, 2)
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
        XCTAssertEqual(antigravity.fetches, 1, "the open's read of Codex armed the 10 s debounce")

        clock.advance(11)
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
