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

    func testManualRefreshQueuesOneFollowUpForAnActiveBackgroundRead() async {
        let clock = ManualClock()
        let provider = HangingProvider(.codex)
        defer { provider.release() }
        let coordinator = UsageCoordinator(providers: [provider], cache: nil, clock: clock)
        await coordinator.setEnabledTools([.codex])

        let automatic = Task { await coordinator.refresh(.popoverOpened) }
        await waitUntil { provider.fetches == 1 }
        await coordinator.refresh(.manual)
        clock.advance(11)
        await coordinator.refresh(.manual)
        XCTAssertEqual(provider.fetches, 1, "manual reads cannot run beside the background read")

        provider.release()
        await automatic.value
        await waitUntil { provider.fetches == 2 }
        XCTAssertEqual(provider.fetches, 2, "the queued manual refresh runs once")
        await coordinator.refresh(.manual)
        XCTAssertEqual(provider.fetches, 2, "debounce starts when the queued read starts")
    }

    func testPopoverOpenDuringATargetedReadRefreshesTheOtherAccount() async {
        let clock = ManualClock()
        let claude = HangingProvider(.claude)
        defer { claude.release() }
        let codex = CountingProvider(.codex)
        let coordinator = UsageCoordinator(providers: [claude, codex], cache: nil, clock: clock)
        await coordinator.setEnabledTools([.claude, .codex])
        let targeted = Task {
            await coordinator.refresh(.toolsChanged, account: UsageAccountKey(tool: .claude))
        }
        await waitUntil { claude.fetches == 1 }

        await coordinator.refresh(.popoverOpened)
        XCTAssertEqual(claude.fetches, 1)
        XCTAssertEqual(codex.fetches, 1)
        claude.release()
        await targeted.value
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
        await waitUntil { hung.fetches == 2 && claudeState()?.isRefreshing == false }
        XCTAssertEqual(claudeState()?.lastRefreshFailed, false, "a late success still lands")
        XCTAssertNotNil(claudeState()?.snapshot)
        XCTAssertTrue(hung.sawCancellation, "the deadline cancels the read")
        XCTAssertEqual(hung.fetches, 2, "the manual refresh waits until the timed-out read really ends")
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

    func testUnreadableCodexAccountWindowKeepsTheSnapshotAndTheDiskCache() async throws {
        let directory = FileManager.default.temporaryDirectory.appending(path: UUID().uuidString, directoryHint: .isDirectory)
        defer { try? FileManager.default.removeItem(at: directory) }
        let cache = UsageCacheStore(directory: directory)
        let transport = FakeTransport()
        transport.enqueue(.status(200, #"{"rate_limit":{"primary_window":{"used_percent":42,"limit_window_seconds":604800}}}"#))
        transport.enqueue(.status(200, #"""
        {"rate_limit":{"primary_window":{"limit_window_seconds":604800}},
         "additional_rate_limits":[{"limit_name":"GPT Reserve",
           "rate_limit":{"primary_window":{"used_percent":30,"limit_window_seconds":18000}}}]}
        """#))
        let provider = HTTPUsageProvider(tool: .codex, credentials: FakeCredentials(.available(ToolCredential(accessToken: "a"))),
                                         endpoint: CodexEndpoint(), policy: .codex, transport: transport)
        let coordinator = UsageCoordinator(providers: [provider], cache: cache)
        let recorder = StatesRecorder()
        await coordinator.setEnabledTools([.codex])
        await coordinator.setListener { recorder.append($0) }

        await coordinator.refresh(.launch)
        let previous = try XCTUnwrap(recorder.all.last?.first?.snapshot)
        XCTAssertEqual(previous.windows.map(\.id), ["weekly"])
        let cached = cache.load()
        XCTAssertEqual(cached.count, 1)

        await coordinator.refresh(.manual)
        let state = try XCTUnwrap(recorder.all.last?.first)
        XCTAssertTrue(state.lastRefreshFailed)
        XCTAssertEqual(state.snapshot, previous)
        XCTAssertEqual(cache.load(), cached)
    }
}

final class CoordinatorRehydrationTests: XCTestCase {
    func testClaudePlanPersistsWithoutAValidUsageResponse() async throws {
        let directory = FileManager.default.temporaryDirectory.appending(path: UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: directory) }
        let cache = UsageCacheStore(directory: directory)
        let transport = FakeTransport()
        let clock = ManualClock()
        let source = ClaudePlanSource(FakeCredentials(.available(ToolCredential(accessToken: "token"))),
                                      transport: transport, clock: clock)
        let provider = HTTPUsageProvider(tool: .claude, credentials: source, endpoint: ClaudeEndpoint(),
                                         policy: .claude, transport: transport, clock: clock)
        let coordinator = UsageCoordinator(providers: [provider], cache: cache, clock: clock)
        await coordinator.setEnabledTools([.claude])
        transport.enqueue(.status(200, #"{"account":{"uuid":"a"},"organization":{"uuid":"org","plan_display_name":"Pro"}}"#))
        transport.enqueue(.status(200, #"{"five_hour":{"utilization":40}}"#))
        await coordinator.refresh(.launch)
        let first = try XCTUnwrap(cache.load().first)

        clock.advance(86_400)
        transport.enqueue(.status(200, #"{"account":{"uuid":"a"},"organization":{"uuid":"org","plan_display_name":"Ultra"}}"#))
        transport.enqueue(.status(200, #"{"error":"unavailable"}"#))
        await coordinator.refresh(.manual)
        let updated = try XCTUnwrap(cache.load().first)
        XCTAssertEqual(updated.plan, "Ultra")
        XCTAssertEqual(updated.windows, first.windows)
        XCTAssertEqual(updated.capturedAt, first.capturedAt)

        let newSource = ClaudePlanSource(FakeCredentials(.available(ToolCredential(accessToken: "token"))),
                                         transport: transport, clock: clock)
        let newProvider = HTTPUsageProvider(tool: .claude, credentials: newSource, endpoint: ClaudeEndpoint(),
                                            policy: .claude, transport: transport, clock: clock)
        let restarted = UsageCoordinator(providers: [newProvider], cache: cache, clock: clock)
        await restarted.setEnabledTools([.claude])
        transport.enqueue(.status(200, #"{"account":{"uuid":"a"},"organization":{"uuid":"org","plan_display_name":"Max 5x"}}"#))
        transport.enqueue(.status(429, "{}"))
        await restarted.refresh(.launch)
        let cold = try XCTUnwrap(cache.load().first)
        XCTAssertEqual(cold.plan, "Max 5x")
        XCTAssertEqual(cold.accountIdentity, UsageIdentity.hash(subject: "a\0org", token: ""))
        XCTAssertEqual(cold.windows, first.windows)
        XCTAssertEqual(cold.capturedAt, first.capturedAt)
    }

    func testFailedUsageAfterAccountSwitchRemovesPreviousSnapshot() async throws {
        let directory = FileManager.default.temporaryDirectory.appending(path: UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: directory) }
        let cache = UsageCacheStore(directory: directory)
        let transport = FakeTransport()
        var first = ToolCredential(accessToken: "token-a")
        first.accountIdentity = "account-a"
        let credentials = FakeCredentials(.available(first))
        let provider = HTTPUsageProvider(tool: .claude, credentials: credentials, endpoint: ClaudeEndpoint(),
                                         policy: .claude, transport: transport)
        let coordinator = UsageCoordinator(providers: [provider], cache: cache)
        let recorder = StatesRecorder()
        await coordinator.setEnabledTools([.claude])
        await coordinator.setListener { recorder.append($0) }
        transport.enqueue(.status(200, #"{"five_hour":{"utilization":40}}"#))
        await coordinator.refresh(.launch)
        XCTAssertEqual(cache.load().first?.accountIdentity, "account-a")

        var next = ToolCredential(accessToken: "token-b")
        next.accountIdentity = "account-b"
        credentials.set(.available(next))
        transport.enqueue(.status(503, "{}"))
        await coordinator.refresh(.manual)
        XCTAssertNil(recorder.all.last?.first?.snapshot)
        XCTAssertTrue(cache.load().isEmpty)
    }

    func testCopilotPlanOnlyResponseClearsPriorAuthenticationFailure() async {
        let transport = FakeTransport()
        let provider = HTTPUsageProvider(tool: .githubCopilot,
                                         credentials: FakeCredentials(.available(ToolCredential(accessToken: "token"))),
                                         endpoint: GitHubCopilotEndpoint(), policy: FetchPolicy(), transport: transport)
        let coordinator = UsageCoordinator(providers: [provider], cache: nil)
        let recorder = StatesRecorder()
        await coordinator.setEnabledTools([.githubCopilot])
        await coordinator.setListener { recorder.append($0) }
        transport.enqueue(.status(401, "{}"))
        await coordinator.refresh(.launch)
        XCTAssertEqual(recorder.all.last?.first?.auth, .rejected)

        transport.enqueue(.status(200, #"{"copilot_plan":"individual"}"#))
        await coordinator.refresh(.manual)
        XCTAssertEqual(recorder.all.last?.first?.auth, .signedIn)
        XCTAssertEqual(recorder.all.last?.first?.snapshot?.windows.count, 0)
    }

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

    func testAnyRefreshThatFetchesArmsThePopoverAndResumeDebounce() async {
        let arming: [RefreshReason] = [.launch, .scheduled, .toolsChanged, .popoverOpened, .systemResumed, .manual]
        for reason in arming {
            for debounced in [RefreshReason.popoverOpened, .systemResumed] {
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
    func testForcedRefreshDebouncesOnlyTheRecentlyAttemptedAccount() async {
        for reason in [RefreshReason.popoverOpened, .systemResumed] {
            let clock = ManualClock()
            let claude = CountingProvider(.claude)
            let codex = CountingProvider(.codex)
            let coordinator = UsageCoordinator(providers: [claude, codex], cache: nil, clock: clock)
            await coordinator.setEnabledTools([.claude, .codex])

            await coordinator.refresh(.toolsChanged, account: UsageAccountKey(tool: .claude))
            await coordinator.refresh(reason)

            XCTAssertEqual(claude.fetches, 1)
            XCTAssertEqual(codex.fetches, 1)
        }
    }

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
        XCTAssertEqual(codex.fetches, 3, "a recent background read does not debounce a manual refresh")

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
        transport.enqueue(.status(200, #"{"status":{"indicator":"none","description":"All Systems Operational"}}"#))
        let clock = ManualClock()
        let service = ProviderStatusService(transport: transport, clock: clock)
        await service.setTools([.claude])
        clock.advance(16 * 60)
        await service.poll() // FakeTransport fails once its queue is empty
        let recorder = StatusRecorder()
        await service.setListener { recorder.value = $0 }
        await service.setTools([.claude])
        let status = recorder.value[.claude]
        XCTAssertEqual(status?.level, .operational)
        XCTAssertEqual(status?.lastCheckFailed, true)
        XCTAssertTrue(status.map { $0.detail.hasPrefix($0.label) && $0.detail.contains($0.summary) } == true)
    }

    func testReappearingToolChecksAgainAfterItsFirstResultWasDiscarded() async {
        let transport = SuspendedStatusTransport()
        let service = ProviderStatusService(transport: transport, clock: ManualClock())
        let recorder = StatusRecorder()
        await service.setListener { recorder.value = $0 }
        let first = Task { await service.setTools([.codex]) }
        await transport.waitForFirstRequest()
        await service.setTools([])
        await transport.releaseFirstRequest()
        await first.value
        XCTAssertNil(recorder.value[.codex])

        await service.setTools([.codex])
        let count = await transport.requests
        XCTAssertEqual(count, 2)
        XCTAssertEqual(recorder.value[.codex]?.level, .major)
    }

    func testReappearingToolQueuesAStatusReadWhileItsFirstReadIsInFlight() async {
        let transport = SuspendedStatusTransport()
        let service = ProviderStatusService(transport: transport, clock: ManualClock())
        let recorder = StatusRecorder()
        await service.setListener { recorder.value = $0 }
        let first = Task { await service.setTools([.codex]) }
        await transport.waitForFirstRequest()
        await service.setTools([])
        await service.setTools([.codex])
        await transport.releaseFirstRequest()
        await first.value

        let count = await transport.requests
        XCTAssertEqual(count, 2)
        XCTAssertEqual(recorder.value[.codex]?.level, .major)
    }
}

private actor SuspendedStatusTransport: HTTPTransport {
    private(set) var requests = 0
    private var waitingForStart: CheckedContinuation<Void, Never>?
    private var waitingToFinish: CheckedContinuation<Void, Never>?

    func waitForFirstRequest() async {
        if requests == 0 {
            await withCheckedContinuation { waitingForStart = $0 }
        }
    }

    func releaseFirstRequest() {
        waitingToFinish?.resume()
        waitingToFinish = nil
    }

    func send(_ request: URLRequest) async throws -> (Data, HTTPURLResponse) {
        requests += 1
        let first = requests == 1
        if first {
            await withCheckedContinuation { continuation in
                waitingToFinish = continuation
                waitingForStart?.resume()
                waitingForStart = nil
            }
        }
        let json = first ? #"{"status":{"indicator":"none"}}"# : #"{"status":{"indicator":"major"}}"#
        let response = HTTPURLResponse(url: request.url!, statusCode: 200, httpVersion: nil, headerFields: nil)!
        return (Data(json.utf8), response)
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
