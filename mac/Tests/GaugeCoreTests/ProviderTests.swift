import Foundation
import XCTest
@testable import GaugeCore

final class ManualClock: MonotonicClock, @unchecked Sendable {
    private let lock = NSLock()
    private var value: TimeInterval = 1000

    func now() -> TimeInterval { lock.withLock { value } }
    func advance(_ seconds: TimeInterval) { lock.withLock { value += seconds } }
}

/// Replays queued responses: a status code with a body, or a thrown error.
final class FakeTransport: HTTPTransport, @unchecked Sendable {
    enum Reply {
        case status(Int, String, headers: [String: String] = [:])
        case failure(any Error)
    }

    private let lock = NSLock()
    private var replies: [Reply] = []
    private(set) var requestCount = 0
    private(set) var lastAuthorization: String?
    private(set) var lastRequest: URLRequest?

    func enqueue(_ reply: Reply) { lock.withLock { replies.append(reply) } }

    func send(_ request: URLRequest) async throws -> (Data, HTTPURLResponse) {
        let reply: Reply = lock.withLock {
            requestCount += 1
            lastAuthorization = request.value(forHTTPHeaderField: "Authorization")
            lastRequest = request
            return replies.isEmpty ? .failure(URLError(.notConnectedToInternet)) : replies.removeFirst()
        }
        switch reply {
        case .status(let code, let body, let headers):
            let response = HTTPURLResponse(url: request.url!, statusCode: code, httpVersion: nil, headerFields: headers)!
            return (Data(body.utf8), response)
        case .failure(let error):
            throw error
        }
    }
}

final class FakeCredentials: CredentialSource, @unchecked Sendable {
    private let lock = NSLock()
    private var current: CredentialRead

    init(_ read: CredentialRead) { current = read }

    func set(_ read: CredentialRead) { lock.withLock { current = read } }
    func read(interactive: Bool) async -> CredentialRead { lock.withLock { current } }
}

final class FakeRefresher: DelegatedRefreshing, @unchecked Sendable {
    private let lock = NSLock()
    private(set) var calls = 0
    var onRefresh: (() -> Void)?

    func tryRefresh() async -> Bool {
        lock.withLock { calls += 1 }
        onRefresh?()
        return true
    }
}

private let claudeBody = #"{"five_hour":{"utilization":40,"resets_at":"2026-10-19T05:00:00Z"}}"#

final class HTTPUsageProviderTests: XCTestCase {
    func testClaudeRefreshesTheServerPlanDailyEvenWhenUsageIsThrottled() async throws {
        let transport = FakeTransport()
        let clock = ManualClock()
        let credentials = ClaudePlanSource(
            FakeCredentials(.available(ToolCredential(accessToken: "token", plan: "Max 5x"))),
            transport: transport, clock: clock)
        let provider = HTTPUsageProvider(tool: .claude, credentials: credentials, endpoint: ClaudeEndpoint(),
                                         policy: .claude, transport: transport, clock: clock)
        transport.enqueue(.status(200, #"{"organization":{"plan_display_name":"Pro"}}"#))
        transport.enqueue(.status(200, claudeBody))
        let first = try await provider.fetch(.background)
        XCTAssertEqual(first.snapshot.plan, "Pro")

        clock.advance(60)
        let cached = try await provider.fetch(.background)
        XCTAssertEqual(cached.snapshot.plan, "Pro")
        XCTAssertEqual(transport.requestCount, 2)

        clock.advance(24 * 60 * 60 - 60)
        transport.enqueue(.status(200, #"{"organization":{"plan_display_name":"Ultra"}}"#))
        transport.enqueue(.status(429, "{}"))
        let updated = try await provider.fetch(.background)
        XCTAssertEqual(updated.snapshot.plan, "Ultra")
        XCTAssertEqual(updated.snapshot.capturedAt, first.snapshot.capturedAt)
        XCTAssertFalse(updated.isLive)
        XCTAssertEqual(transport.requestCount, 4)
    }

    func testCodexCreditDetailFailureKeepsKnownDeadlinesOnlyForAnUnchangedBalance() async throws {
        let transport = FakeTransport()
        let clock = ManualClock()
        let endpoint = CodexEndpoint(clock: clock)
        let credential = ToolCredential(accessToken: "token")
        let now = Date()
        transport.enqueue(usageWithCredits(1))
        transport.enqueue(creditDeadlines)
        let first = try await endpoint.fetch(credential, transport: transport, now: now)
        let deadline = try XCTUnwrap(first.resetCreditDeadlines?.first)

        clock.advance(600)
        transport.enqueue(usageWithCredits(1))
        transport.enqueue(.status(429, "", headers: ["Retry-After": "3600"]))
        let throttled = try await endpoint.fetch(credential, transport: transport, now: now + 600)
        XCTAssertEqual(throttled.resetCreditDeadlines, [deadline])

        clock.advance(660)
        for balance in [1, 2, 1] {
            transport.enqueue(usageWithCredits(balance))
        }
        let sameBalance = try await endpoint.fetch(credential, transport: transport, now: now + 1260)
        let changedBalance = try await endpoint.fetch(credential, transport: transport, now: now + 1260)
        let restoredBalance = try await endpoint.fetch(credential, transport: transport, now: now + 1260)
        XCTAssertEqual(sameBalance.resetCreditDeadlines, [deadline])
        XCTAssertNil(changedBalance.resetCreditDeadlines)
        XCTAssertNil(restoredBalance.resetCreditDeadlines)
        XCTAssertEqual(transport.requestCount, 7)
    }

    func testCodexCreditCooldownSurvivesBalanceChangesWithoutBlockingUsage() async throws {
        let transport = FakeTransport()
        let clock = ManualClock()
        let endpoint = CodexEndpoint(clock: clock)
        let credential = ToolCredential(accessToken: "token")
        let now = Date()
        transport.enqueue(usageWithCredits(1))
        transport.enqueue(.status(429, "", headers: ["Retry-After": "3600"]))
        let first = try await endpoint.fetch(credential, transport: transport, now: now)
        XCTAssertEqual(first.windows.count, 1)
        XCTAssertNil(first.resetCreditDeadlines)

        transport.enqueue(usageWithCredits(0))
        let empty = try await endpoint.fetch(credential, transport: transport, now: now)
        XCTAssertEqual(empty.resetCredits, 0)
        clock.advance(3599)
        transport.enqueue(usageWithCredits(2))
        let duringCooldown = try await endpoint.fetch(credential, transport: transport, now: now)
        XCTAssertEqual(duringCooldown.windows.count, 1)
        XCTAssertEqual(duringCooldown.resetCredits, 2)
        XCTAssertEqual(transport.requestCount, 4)

        clock.advance(1)
        transport.enqueue(usageWithCredits(2))
        transport.enqueue(creditDeadlines)
        let recovered = try await endpoint.fetch(credential, transport: transport, now: now)
        XCTAssertEqual(recovered.resetCreditDeadlines?.count, 1)
        XCTAssertEqual(transport.requestCount, 6)
    }

    func testCodexCreditCooldownIsResetWhenTheLoginChanges() async throws {
        let first = ToolCredential(accessToken: "first-token", accountID: "first-workspace")
        let replacements = [ToolCredential(accessToken: "other-token", accountID: "first-workspace"),
                            ToolCredential(accessToken: "first-token", accountID: "other-workspace")]
        for replacement in replacements {
            let transport = FakeTransport()
            let endpoint = CodexEndpoint(clock: ManualClock())
            let now = Date()
            transport.enqueue(usageWithCredits(1))
            transport.enqueue(.status(429, "", headers: ["Retry-After": "3600"]))
            let throttled = try await endpoint.fetch(first, transport: transport, now: now)
            XCTAssertNil(throttled.resetCreditDeadlines)

            transport.enqueue(usageWithCredits(1))
            transport.enqueue(creditDeadlines)
            let switched = try await endpoint.fetch(replacement, transport: transport, now: now)
            XCTAssertEqual(switched.resetCreditDeadlines?.count, 1)
            XCTAssertEqual(transport.requestCount, 4)
        }
    }

    private func usageWithCredits(_ balance: Int) -> FakeTransport.Reply {
        .status(200, #"{"rate_limit":{"primary_window":{"used_percent":25}},"rate_limit_reset_credits":{"available_count":\#(balance)}}"#)
    }

    private let creditDeadlines = FakeTransport.Reply.status(200,
        #"{"credits":[{"status":"available","expires_at":"2027-03-09T07:59:00Z"}]}"#)

    private func makeProvider(
        policy: FetchPolicy, transport: FakeTransport, credentials: FakeCredentials,
        refresher: FakeRefresher? = nil, clock: ManualClock = ManualClock()
    ) -> HTTPUsageProvider {
        HTTPUsageProvider(tool: .claude, credentials: credentials, endpoint: ClaudeEndpoint(), policy: policy,
                          transport: transport, refresher: refresher, clock: clock)
    }

    func testMissingCredentialIsCleanSignedOutSnapshot() async throws {
        let transport = FakeTransport()
        let provider = makeProvider(policy: .claude, transport: transport, credentials: FakeCredentials(.missing))
        let outcome = try await provider.fetch(.background)
        XCTAssertFalse(outcome.signedIn)
        XCTAssertTrue(outcome.snapshot.windows.isEmpty)
        XCTAssertEqual(transport.requestCount, 0)
    }

    func testClaudeMinimumIntervalServesCacheEvenForUser() async throws {
        let transport = FakeTransport()
        transport.enqueue(.status(200, claudeBody))
        let clock = ManualClock()
        let provider = makeProvider(policy: .claude, transport: transport,
                                    credentials: FakeCredentials(.available(ToolCredential(accessToken: "a"))), clock: clock)
        let first = try await provider.fetch(.background)
        XCTAssertTrue(first.isLive)
        clock.advance(60)
        let second = try await provider.fetch(.user)
        XCTAssertFalse(second.isLive)
        XCTAssertEqual(second.snapshot.windows, first.snapshot.windows)
        XCTAssertEqual(transport.requestCount, 1)
    }

    func testRetryableStatusServesCacheAndCooldownBlocksOnlyBackground() async throws {
        let transport = FakeTransport()
        let clock = ManualClock()
        let provider = makeProvider(policy: .codexLikeForTests, transport: transport,
                                    credentials: FakeCredentials(.available(ToolCredential(accessToken: "a"))), clock: clock)
        transport.enqueue(.status(200, claudeBody))
        _ = try await provider.fetch(.background)

        transport.enqueue(.status(429, "", headers: ["Retry-After": "600"]))
        let throttled = try await provider.fetch(.background)
        XCTAssertFalse(throttled.isLive)
        XCTAssertEqual(transport.requestCount, 2)

        clock.advance(60)
        _ = try await provider.fetch(.background)
        XCTAssertEqual(transport.requestCount, 2, "background fetch inside the cooldown must not hit the network")

        transport.enqueue(.status(200, claudeBody))
        let manual = try await provider.fetch(.user)
        XCTAssertTrue(manual.isLive)
        XCTAssertEqual(transport.requestCount, 3)
    }

    func testCooldownWithNothingCachedFailsFastWithoutNetwork() async throws {
        let transport = FakeTransport()
        let provider = makeProvider(policy: .codexLikeForTests, transport: transport,
                                    credentials: FakeCredentials(.available(ToolCredential(accessToken: "a"))))
        transport.enqueue(.status(503, ""))
        do {
            _ = try await provider.fetch(.background)
            XCTFail("expected the cold-start failure to propagate")
        } catch {}
        do {
            _ = try await provider.fetch(.background)
            XCTFail("expected a cooldown failure")
        } catch let error as UsageFetchError {
            XCTAssertEqual(error, .cooldown)
        }
        XCTAssertEqual(transport.requestCount, 1)
    }

    func testRejectedTokenRefreshesAndRetriesOnce() async throws {
        let transport = FakeTransport()
        let credentials = FakeCredentials(.available(ToolCredential(accessToken: "old")))
        let refresher = FakeRefresher()
        refresher.onRefresh = { credentials.set(.available(ToolCredential(accessToken: "new"))) }
        let provider = makeProvider(policy: .codexLikeForTests, transport: transport, credentials: credentials, refresher: refresher)
        transport.enqueue(.status(401, ""))
        transport.enqueue(.status(200, claudeBody))
        let outcome = try await provider.fetch(.background)
        XCTAssertTrue(outcome.isLive)
        XCTAssertEqual(refresher.calls, 1)
        XCTAssertEqual(transport.lastAuthorization, "Bearer new")
    }

    func testExpiredTokenDelegatesRefreshBeforeFetching() async throws {
        let transport = FakeTransport()
        let credentials = FakeCredentials(.invalid(.expired))
        let refresher = FakeRefresher()
        refresher.onRefresh = { credentials.set(.available(ToolCredential(accessToken: "fresh"))) }
        let provider = makeProvider(policy: .codexLikeForTests, transport: transport, credentials: credentials, refresher: refresher)
        transport.enqueue(.status(200, claudeBody))
        _ = try await provider.fetch(.background)
        XCTAssertEqual(transport.lastAuthorization, "Bearer fresh")
    }

    func testUnrefreshableTokenAsksForSignInWithoutSpawningTheCLI() async throws {
        let credentials = FakeCredentials(.invalid(.expiredUnrefreshable))
        let refresher = FakeRefresher()
        let provider = makeProvider(policy: .claude, transport: FakeTransport(), credentials: credentials, refresher: refresher)
        do {
            _ = try await provider.fetch(.user)
            XCTFail("expected a sign-in requirement")
        } catch UsageFetchError.authenticationRequired {
        }
        XCTAssertEqual(refresher.calls, 0)
    }

    func testAccountSwitchDropsCache() async throws {
        let transport = FakeTransport()
        let credentials = FakeCredentials(.available(ToolCredential(accessToken: "a")))
        let provider = makeProvider(policy: .claude, transport: transport, credentials: credentials)
        transport.enqueue(.status(200, claudeBody))
        _ = try await provider.fetch(.background)
        credentials.set(.available(ToolCredential(accessToken: "b")))
        transport.enqueue(.status(200, claudeBody))
        let outcome = try await provider.fetch(.background)
        XCTAssertTrue(outcome.isLive, "a different account must not be served the previous account's cache")
    }

    private func expectUnreadable(_ provider: HTTPUsageProvider, _ problem: CredentialProblem) async throws {
        do {
            _ = try await provider.fetch(.background)
            XCTFail("expected the read failure to propagate")
        } catch let error as UsageFetchError {
            XCTAssertEqual(error, .credentialUnreadable(problem))
        }
    }

    func testUnseenCredentialKeepsTheHappyPathCap() async throws {
        for problem in [CredentialProblem.unreadable, .keychainDenied] {
            let transport = FakeTransport()
            transport.enqueue(.status(200, claudeBody))
            let credentials = FakeCredentials(.available(ToolCredential(accessToken: "a")))
            let clock = ManualClock()
            let provider = makeProvider(policy: .claude, transport: transport, credentials: credentials, clock: clock)
            let first = try await provider.fetch(.background)

            credentials.set(.invalid(problem))
            try await expectUnreadable(provider, problem)

            credentials.set(.available(ToolCredential(accessToken: "a")))
            clock.advance(60)
            let served = try await provider.fetch(.background)
            XCTAssertFalse(served.isLive)
            XCTAssertEqual(served.snapshot.windows, first.snapshot.windows)
            XCTAssertEqual(transport.requestCount, 1)
        }
    }

    func testUnseenCredentialKeepsTheCooldown() async throws {
        for problem in [CredentialProblem.unreadable, .keychainDenied] {
            let transport = FakeTransport()
            transport.enqueue(.status(200, claudeBody))
            transport.enqueue(.status(429, "", headers: ["Retry-After": "600"]))
            let credentials = FakeCredentials(.available(ToolCredential(accessToken: "a")))
            let clock = ManualClock()
            let provider = makeProvider(policy: .codexLikeForTests, transport: transport, credentials: credentials, clock: clock)
            _ = try await provider.fetch(.background)
            _ = try await provider.fetch(.background)
            XCTAssertEqual(transport.requestCount, 2)

            credentials.set(.invalid(problem))
            try await expectUnreadable(provider, problem)

            credentials.set(.available(ToolCredential(accessToken: "a")))
            clock.advance(60)
            let served = try await provider.fetch(.background)
            XCTAssertFalse(served.isLive)
            XCTAssertEqual(transport.requestCount, 2, "the cooldown must outlast the failed read")
        }
    }

    func testAccountSwitchBehindAnUnseenCredentialStillDropsCache() async throws {
        let transport = FakeTransport()
        let credentials = FakeCredentials(.available(ToolCredential(accessToken: "a")))
        let provider = makeProvider(policy: .claude, transport: transport, credentials: credentials)
        transport.enqueue(.status(200, claudeBody))
        _ = try await provider.fetch(.background)

        credentials.set(.invalid(.unreadable))
        try await expectUnreadable(provider, .unreadable)

        credentials.set(.available(ToolCredential(accessToken: "b")))
        transport.enqueue(.status(200, claudeBody))
        let outcome = try await provider.fetch(.background)
        XCTAssertTrue(outcome.isLive, "the switch shows as soon as the credential can be read again")
    }

    func testExpiredCredentialStillCountsAsAChange() async throws {
        let transport = FakeTransport()
        let credentials = FakeCredentials(.available(ToolCredential(accessToken: "a")))
        let provider = makeProvider(policy: .claude, transport: transport, credentials: credentials)
        transport.enqueue(.status(200, claudeBody))
        _ = try await provider.fetch(.background)

        credentials.set(.invalid(.expiredUnrefreshable))
        do {
            _ = try await provider.fetch(.background)
            XCTFail("expected a sign-in requirement")
        } catch UsageFetchError.authenticationRequired {
        }

        credentials.set(.available(ToolCredential(accessToken: "a")))
        transport.enqueue(.status(200, claudeBody))
        let outcome = try await provider.fetch(.background)
        XCTAssertTrue(outcome.isLive, "a read that saw the document keeps today's behaviour")
    }
}

extension FetchPolicy {
    static let codexLikeForTests = FetchPolicy(retryBackoff: BackoffPolicy(base: 120, maximum: 1800))
}

final class RateLimitGateTests: XCTestCase {
    func testBackoffEscalatesAndCaps() {
        let backoff = BackoffPolicy(base: 120, maximum: 1800)
        XCTAssertEqual([1, 2, 3, 4, 5, 9].map(backoff.cooldown(forAttempt:)), [120, 240, 480, 960, 1800, 1800])
    }

    func testRetryAfterIsClampedAndNeverShortensABlock() {
        var gate = RateLimitGate(backoff: BackoffPolicy(base: 120, maximum: 1800))
        XCTAssertEqual(gate.recordFailure(retryAfter: 99_999, now: 0), 3600)
        XCTAssertEqual(gate.recordFailure(retryAfter: 10, now: 100), 3500)
        XCTAssertTrue(gate.shouldBlock(.background, now: 200))
        XCTAssertFalse(gate.shouldBlock(.user, now: 200))
        gate.recordSuccess()
        XCTAssertFalse(gate.inCooldown(now: 200))
    }
}

final class ClaudeKeychainSourceTests: XCTestCase {
    final class FakeKeychain: KeychainReading, @unchecked Sendable {
        let lock = NSLock()
        var item: KeychainItem?
        var lookupFailure: Int32?
        var result: KeychainPasswordResult = .notFound
        var passwordReads = 0
        var lookupServices: [String] = []
        var passwordServices: [String] = []

        func newestItem(service: String) async -> KeychainItemLookup {
            lock.withLock { () -> KeychainItemLookup in
                lookupServices.append(service)
                if let lookupFailure { return .failed(lookupFailure) }
                return item.map(KeychainItemLookup.found) ?? .notFound
            }
        }
        func password(service: String, account: String?) async -> KeychainPasswordResult {
            lock.withLock {
                passwordReads += 1
                passwordServices.append(service)
                return result
            }
        }
    }

    private let document = Data(#"{"claudeAiOauth":{"accessToken":"tok","subscriptionType":"pro"}}"#.utf8)

    func testAdditionalClaudeAccountReadsOnlyItsScopedKeychainService() async {
        let keychain = FakeKeychain()
        keychain.item = KeychainItem(account: "me", modified: Date(timeIntervalSince1970: 1))
        keychain.result = .data(document)
        let directory = URL(fileURLWithPath: "/agentgauge-test/work-profile", isDirectory: true)
        let source = ClaudeCredentialSource(keychain: keychain, environment: [:], scopedConfigDirectory: directory)

        let read = await source.read(interactive: false)
        guard case .available(let credential) = read else { return XCTFail("expected a credential") }
        XCTAssertEqual(credential.plan, "Pro")
        XCTAssertEqual(credential.historyIdentity, UsageIdentity.hash(subject: nil, token: "tok"))
        XCTAssertEqual(keychain.lookupServices, ["Claude Code-credentials-189f7b27"])
        XCTAssertEqual(keychain.passwordServices, keychain.lookupServices)
    }

    func testCodexHistorySeparatesUsersInOneWorkspaceAndKeepsTokenRotationTogether() {
        func historyIdentity(user: String, token: String) -> String? {
            let claims = Data(#"{"sub":"\#(user)"}"#.utf8).base64EncodedString()
            let data = Data(#"{"tokens":{"id_token":"e30.\#(claims).sig","access_token":"\#(token)","account_id":"team"}}"#.utf8)
            guard case .available(let credential) = CredentialParsing.codex(from: data, now: Date(), scopeHistory: true)
            else { return nil }
            return credential.historyIdentity
        }
        let alice = historyIdentity(user: "alice", token: "first")
        XCTAssertNotNil(alice)
        XCTAssertEqual(alice, historyIdentity(user: "alice", token: "rotated"))
        XCTAssertNotEqual(alice, historyIdentity(user: "bob", token: "new-login"))
    }

    func testSecretIsReadAgainOnlyWhenTheItemChanges() async {
        let keychain = FakeKeychain()
        keychain.item = KeychainItem(account: "me", modified: Date(timeIntervalSince1970: 1))
        keychain.result = .data(document)
        let source = ClaudeCredentialSource(keychain: keychain, environment: [:], homeDirectory: URL(fileURLWithPath: "/nonexistent"))

        _ = await source.read(interactive: false)
        _ = await source.read(interactive: false)
        XCTAssertEqual(keychain.passwordReads, 1)

        keychain.item = KeychainItem(account: "me", modified: Date(timeIntervalSince1970: 2))
        let read = await source.read(interactive: false)
        XCTAssertEqual(keychain.passwordReads, 2)
        guard case .available(let credential) = read else { return XCTFail("expected a credential") }
        XCTAssertEqual(credential.plan, "Pro")
    }

    func testProfilePlanOverridesTheStaleCredentialPlan() async throws {
        let home = FileManager.default.temporaryDirectory.appending(path: UUID().uuidString, directoryHint: .isDirectory)
        try FileManager.default.createDirectory(at: home, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: home) }
        let keychain = FakeKeychain()
        keychain.item = KeychainItem(account: "me", modified: Date(timeIntervalSince1970: 1))
        keychain.result = .data(Data(#"{"claudeAiOauth":{"accessToken":"tok","subscriptionType":"max","rateLimitTier":"default_claude_max_5x"}}"#.utf8))
        let source = ClaudeCredentialSource(keychain: keychain, environment: [:], homeDirectory: home)
        let profile = home.appending(path: ".claude.json")

        guard case .available(let before) = await source.read(interactive: false) else { return XCTFail("expected a credential") }
        XCTAssertEqual(before.plan, "Max 5x")

        try Data(#"{"oauthAccount":{"accountUuid":"account","organizationType":"claude_max","organizationRateLimitTier":"default_claude_max_20x"}}"#.utf8).write(to: profile)
        guard case .available(let after) = await source.read(interactive: false) else { return XCTFail("expected a credential") }
        XCTAssertEqual(after.plan, "Ultra")
        XCTAssertNil(after.accountIdentity)
        XCTAssertEqual(keychain.passwordReads, 1, "the profile is applied on top of the cached secret")

        try Data("{ not json".utf8).write(to: profile)
        guard case .available(let broken) = await source.read(interactive: false) else { return XCTFail("expected a credential") }
        XCTAssertEqual(broken.plan, "Max 5x")
    }

    func testProfilePlanIgnoresTeamAndEnterpriseAccounts() {
        let team = Data(#"{"oauthAccount":{"organizationType":"claude_team"}}"#.utf8)
        XCTAssertNil(CredentialParsing.claudeProfilePlan(from: team))
    }

    final class Clock: @unchecked Sendable {
        let lock = NSLock()
        var current: Date
        init(_ current: Date) { self.current = current }
        var now: Date { lock.withLock { current } }
    }

    func testCachedStrippedTokenExpiresAsUnrefreshable() async {
        let keychain = FakeKeychain()
        keychain.item = KeychainItem(account: "me", modified: Date(timeIntervalSince1970: 1))
        // Valid when first read, without the fields Claude Code needs to refresh it.
        keychain.result = .data(Data(#"{"claudeAiOauth":{"accessToken":"tok","refreshToken":"r","expiresAt":2000000}}"#.utf8))
        let clock = Clock(Date(timeIntervalSince1970: 1000))
        let source = ClaudeCredentialSource(keychain: keychain, environment: [:],
                                            homeDirectory: URL(fileURLWithPath: "/nonexistent"), now: { clock.now })

        guard case .available = await source.read(interactive: false) else { return XCTFail("expected a credential") }
        clock.lock.withLock { clock.current = Date(timeIntervalSince1970: 3000) }
        let expired = await source.read(interactive: false)
        XCTAssertEqual(expired, .invalid(.expiredUnrefreshable), "the item is unchanged, so the cache must keep its refreshability")
        XCTAssertEqual(keychain.passwordReads, 1)
    }

    func testDenialIsOnlyReaskedInteractively() async {
        let keychain = FakeKeychain()
        keychain.item = KeychainItem(account: "me", modified: Date(timeIntervalSince1970: 1))
        keychain.result = .denied
        let source = ClaudeCredentialSource(keychain: keychain, environment: [:], homeDirectory: URL(fileURLWithPath: "/nonexistent"))

        let first = await source.read(interactive: false)
        let second = await source.read(interactive: false)
        XCTAssertEqual(first, .invalid(.keychainDenied))
        XCTAssertEqual(second, .invalid(.keychainDenied))
        XCTAssertEqual(keychain.passwordReads, 1)

        _ = await source.read(interactive: true)
        XCTAssertEqual(keychain.passwordReads, 2)
    }

    final class ScriptedRunner: ProcessRunning, @unchecked Sendable {
        let result: ProcessResult
        private(set) var arguments: [String] = []
        init(_ result: ProcessResult) { self.result = result }
        func run(_ executable: URL, arguments: [String], environment: [String: String],
                 timeout: TimeInterval, captureOutput: Bool) async -> ProcessResult {
            self.arguments = arguments
            return result
        }
    }

    private func toolResult(exit: Int32?, output: String? = nil, timedOut: Bool = false, launched: Bool = true) -> ProcessResult {
        ProcessResult(exitCode: exit, timedOut: timedOut, launched: launched, output: output.map { Data($0.utf8) })
    }

    func testSecurityToolReadsTheItemWithoutTheFramework() async {
        let fallback = FakeKeychain()
        let runner = ScriptedRunner(toolResult(exit: 0, output: #"{"claudeAiOauth":{}}"# + "\n"))
        let keychain = SecurityToolKeychain(runner: runner, fallback: fallback)
        let result = await keychain.password(service: "Claude Code-credentials", account: "me")
        XCTAssertEqual(result, .data(Data(#"{"claudeAiOauth":{}}"#.utf8)))
        XCTAssertEqual(runner.arguments, ["find-generic-password", "-s", "Claude Code-credentials", "-a", "me", "-w"])
        XCTAssertEqual(fallback.passwordReads, 0)
    }

    func testSecurityToolHexOutputIsDecoded() {
        let hex = Data(#"{"a":1}"#.utf8).map { String(format: "%02x", $0) }.joined()
        XCTAssertEqual(SecurityToolKeychain.decodePassword(Data((hex + "\n").utf8)), Data(#"{"a":1}"#.utf8))
        XCTAssertNil(SecurityToolKeychain.decodePassword(Data("not a document".utf8)))
        XCTAssertNil(SecurityToolKeychain.decodePassword(Data("abcd".utf8)), "hex that is not a JSON document")
    }

    func testSecurityToolStatusesMapWithoutFallingBack() {
        XCTAssertEqual(SecurityToolKeychain.interpret(toolResult(exit: 44)), .notFound)
        XCTAssertEqual(SecurityToolKeychain.interpret(toolResult(exit: 128)), .denied)
        XCTAssertEqual(SecurityToolKeychain.interpret(toolResult(exit: 51)), .denied)
        // An unanswered prompt must not be followed by a second one from the fallback.
        XCTAssertEqual(SecurityToolKeychain.interpret(toolResult(exit: nil, timedOut: true)), .denied)
    }

    func testSecurityToolFailuresFallBackToTheFramework() async {
        for failure in [toolResult(exit: nil, launched: false), toolResult(exit: 1), toolResult(exit: 0, output: "garbage")] {
            let fallback = FakeKeychain()
            fallback.result = .data(document)
            let keychain = SecurityToolKeychain(runner: ScriptedRunner(failure), fallback: fallback)
            let result = await keychain.password(service: "Claude Code-credentials", account: nil)
            XCTAssertEqual(result, .data(document))
            XCTAssertEqual(fallback.passwordReads, 1)
        }
    }

    func testNoKeychainItemAndNoFileIsMissing() async {
        let source = ClaudeCredentialSource(keychain: FakeKeychain(), environment: [:], homeDirectory: URL(fileURLWithPath: "/nonexistent"))
        let read = await source.read(interactive: false)
        XCTAssertEqual(read, .missing)
    }

    func testFailedLookupIsUnreadableRatherThanNotSignedIn() async {
        let keychain = FakeKeychain()
        keychain.lookupFailure = -25308 // errSecInteractionNotAllowed
        let source = ClaudeCredentialSource(keychain: keychain, environment: [:], homeDirectory: URL(fileURLWithPath: "/nonexistent"))
        let failed = await source.read(interactive: false)
        XCTAssertEqual(failed, .invalid(.unreadable))

        keychain.lookupFailure = nil
        keychain.item = KeychainItem(account: "me", modified: Date(timeIntervalSince1970: 1))
        keychain.result = .data(document)
        guard case .available = await source.read(interactive: false) else { return XCTFail("expected the failure not to stick") }
    }

    func testUnreadableSecretIsNotHeldAgainstTheItem() async {
        for unreadable in [KeychainPasswordResult.failed(-1), .data(Data("{ half-written".utf8))] {
            let keychain = FakeKeychain()
            keychain.item = KeychainItem(account: "me", modified: Date(timeIntervalSince1970: 1))
            keychain.result = unreadable
            let source = ClaudeCredentialSource(keychain: keychain, environment: [:], homeDirectory: URL(fileURLWithPath: "/nonexistent"))
            let first = await source.read(interactive: false)
            XCTAssertEqual(first, .invalid(.unreadable))

            // Same modification date: a cached failure would hold until the next rotation.
            keychain.result = .data(document)
            guard case .available = await source.read(interactive: false) else { return XCTFail("expected a re-read to recover") }
            XCTAssertEqual(keychain.passwordReads, 2)
        }
    }

    func testSecretMissingBesideAFoundItemIsNotReadAsSignedOut() async {
        let keychain = FakeKeychain()
        keychain.item = KeychainItem(account: "me", modified: Date(timeIntervalSince1970: 1))
        keychain.result = .notFound
        let source = ClaudeCredentialSource(keychain: keychain, environment: [:], homeDirectory: URL(fileURLWithPath: "/nonexistent"))
        let vanishing = await source.read(interactive: false)
        XCTAssertEqual(vanishing, .invalid(.unreadable))

        keychain.item = nil
        let gone = await source.read(interactive: false)
        XCTAssertEqual(gone, .missing, "the lookup finding no item is what means signed out")
    }
}
