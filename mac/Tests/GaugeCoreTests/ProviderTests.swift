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
        var result: KeychainPasswordResult = .notFound
        var passwordReads = 0

        func newestItem(service: String) async -> KeychainItem? { lock.withLock { item } }
        func password(service: String, account: String?) async -> KeychainPasswordResult {
            lock.withLock {
                passwordReads += 1
                return result
            }
        }
    }

    private let document = Data(#"{"claudeAiOauth":{"accessToken":"tok","subscriptionType":"pro"}}"#.utf8)

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
}
