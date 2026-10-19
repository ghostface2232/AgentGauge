import Darwin
import Foundation
import XCTest
@testable import GaugeCore

private let fourWindows = Data("""
{ "response": { "groups": [
    { "buckets": [
        { "bucketId": "gemini-weekly", "window": "weekly", "remainingFraction": 0.99 },
        { "bucketId": "gemini-5h", "window": "5h", "remainingFraction": 0.85 } ] },
    { "buckets": [
        { "bucketId": "3p-weekly", "window": "weekly", "remainingFraction": 1 },
        { "bucketId": "3p-5h", "window": "5h", "remainingFraction": 1 } ] } ] } }
""".utf8)

private let oneWindow = Data("""
{ "response": { "groups": [ { "buckets": [
    { "bucketId": "gemini-5h", "window": "5h", "remainingFraction": 0.5 } ] } ] } }
""".utf8)

private final class FakeReader: AntigravityReader, @unchecked Sendable {
    private let lock = NSLock()
    private let result: AntigravityReading?
    private var calls = 0

    init(_ result: AntigravityReading?) { self.result = result }

    var wasCalled: Bool { lock.withLock { calls > 0 } }

    func reading() async throws -> AntigravityReading? {
        lock.withLock { calls += 1 }
        return result
    }
}

/// Orchestration for `AntigravityProvider`: attach first then delegate, the not-installed empty
/// result, and the failures that preserve the coordinator's cache.
final class AntigravityProviderTests: XCTestCase {
    func testNotInstalledReturnsCleanEmptySnapshotWithoutReading() async throws {
        let attach = FakeReader(AntigravityReading(quotaJSON: fourWindows, plan: "Pro"))
        let provider = AntigravityProvider(readers: [attach], isInstalled: { false })

        let outcome = try await provider.fetch(.background)

        XCTAssertTrue(outcome.snapshot.windows.isEmpty)
        XCTAssertNil(outcome.snapshot.plan)
        XCTAssertEqual(outcome.snapshot.tool, .antigravity)
        XCTAssertFalse(outcome.signedIn)
        XCTAssertFalse(outcome.isLive)
        XCTAssertFalse(attach.wasCalled)
    }

    func testAttachProducesWindowsAndPlanWithoutFallingBackToDelegate() async throws {
        let attach = FakeReader(AntigravityReading(quotaJSON: fourWindows, plan: "Pro"))
        let delegate = FakeReader(nil)
        let provider = AntigravityProvider(readers: [attach, delegate], isInstalled: { true })

        let outcome = try await provider.fetch(.background)

        XCTAssertTrue(outcome.signedIn)
        XCTAssertTrue(outcome.isLive)
        XCTAssertEqual(outcome.snapshot.plan, "Pro")
        XCTAssertEqual(outcome.snapshot.windows.map(\.id), ["gemini-weekly", "gemini-5h", "3p-weekly", "3p-5h"])
        XCTAssertFalse(delegate.wasCalled)
    }

    func testFallsBackToDelegateWhenAttachFindsNothing() async throws {
        let attach = FakeReader(nil)
        let delegate = FakeReader(AntigravityReading(quotaJSON: oneWindow, plan: nil))
        let provider = AntigravityProvider(readers: [attach, delegate], isInstalled: { true })

        let outcome = try await provider.fetch(.user)

        XCTAssertTrue(attach.wasCalled)
        XCTAssertTrue(delegate.wasCalled)
        XCTAssertEqual(outcome.snapshot.windows.map(\.id), ["gemini-5h"])
    }

    func testInstalledButNoEngineReachableThrowsToPreserveCache() async {
        let provider = AntigravityProvider(readers: [FakeReader(nil), FakeReader(nil)], isInstalled: { true })
        do {
            _ = try await provider.fetch(.background)
            XCTFail("expected a failure")
        } catch {
            XCTAssertTrue(error is AntigravityUnavailableError)
        }
    }

    func testAvailabilityOnlyResponseThrowsRatherThanClobberCache() async {
        let reading = AntigravityReading(quotaJSON: Data(#"{ "response": { "groups": [] } }"#.utf8), plan: "Pro")
        let provider = AntigravityProvider(readers: [FakeReader(reading)], isInstalled: { true })
        do {
            _ = try await provider.fetch(.background)
            XCTFail("expected a failure")
        } catch {
            XCTAssertTrue(error is AntigravityUnavailableError)
        }
    }

    func testRichestKeepsTheResponseWithMostWindows() {
        let richest = AntigravityReading(quotaJSON: fourWindows, plan: "rich")
        let readings = [
            AntigravityReading(quotaJSON: oneWindow, plan: nil),
            richest,
            AntigravityReading(quotaJSON: Data(#"{ "response": { "groups": [] } }"#.utf8), plan: nil),
        ]
        XCTAssertEqual(AntigravityAttachReader.richest(readings), richest)
        XCTAssertNil(AntigravityAttachReader.richest([]))
    }

    func testNotInstalledWhenNoBundleUnderTheSearchRoots() async throws {
        let root = FileManager.default.temporaryDirectory.appending(path: "AntigravityNone-\(UUID().uuidString)")
        let outcome = try await AntigravityProvider(searchRoots: [root]).fetch(.background)
        XCTAssertFalse(outcome.signedIn)
        XCTAssertTrue(outcome.snapshot.windows.isEmpty)
    }

    func testDefaultProvidersIncludeAntigravityBetweenCursorAndCopilot() {
        let tools = ProviderFactory.makeDefault().map(\.tool)
        let antigravity = tools.firstIndex(of: .antigravity)
        XCTAssertNotNil(antigravity)
        XCTAssertEqual(tools.firstIndex(of: .cursor).map { $0 + 1 }, antigravity)
        XCTAssertEqual(tools.firstIndex(of: .githubCopilot), antigravity.map { $0 + 1 })
    }

    func testSignInWordingResolves() {
        XCTAssertNotEqual(AuthText.missing(.antigravity), "Auth_Missing_Antigravity")
    }
}

/// A scripted Connect transport: replies keyed by "port/method", recording every call.
private final class FakeConnect: AntigravityConnectTransport, @unchecked Sendable {
    enum Reply {
        case status(Int, String)
        case failure(any Error)
    }

    private let lock = NSLock()
    private let replies: [String: Reply]
    private(set) var calls: [String] = []
    private(set) var tokens: Set<String> = []

    init(_ replies: [String: Reply]) { self.replies = replies }

    func post(port: Int, method: String, body: Data, csrfToken: String) async throws -> AntigravityConnectResponse {
        let key = "\(port)/\(method)"
        let reply: Reply = lock.withLock {
            calls.append(key)
            tokens.insert(csrfToken)
            return replies[key] ?? .failure(URLError(.cannotConnectToHost))
        }
        switch reply {
        case .status(let status, let body): return AntigravityConnectResponse(status: status, body: Data(body.utf8))
        case .failure(let error): throw error
        }
    }
}

final class AntigravityLoopbackClientTests: XCTestCase {
    private let quota = #"{ "response": { "groups": [] } }"#
    private let status = #"{ "userStatus": { "planStatus": { "planInfo": { "planName": "Pro" } } } }"#

    func testSkipsThePlaintextPortAndReadsPlanFromTheWorkingOne() async throws {
        let transport = FakeConnect([
            "2/RetrieveUserQuotaSummary": .status(200, quota),
            "2/GetUserStatus": .status(200, status),
        ])
        let reading = try await AntigravityLoopbackClient(transport: transport).reading(ports: [1, 2], csrfToken: "t")

        XCTAssertEqual(reading?.quotaJSON, Data(quota.utf8))
        XCTAssertEqual(reading?.plan, "Pro")
        XCTAssertEqual(transport.calls, ["1/RetrieveUserQuotaSummary", "2/RetrieveUserQuotaSummary", "2/GetUserStatus"])
        XCTAssertEqual(transport.tokens, ["t"])
    }

    func testFallsBackToTheOlderMethodNameOnlyOn404() async throws {
        let transport = FakeConnect([
            "1/RetrieveUserQuotaSummary": .status(404, ""),
            "1/RetrieveUserQuota": .status(200, quota),
        ])
        let reading = try await AntigravityLoopbackClient(transport: transport).reading(ports: [1], csrfToken: "t")
        XCTAssertEqual(reading?.quotaJSON, Data(quota.utf8))
        XCTAssertNil(reading?.plan) // GetUserStatus failed: best effort, the read still succeeds
    }

    func testAnUnauthorizedPortIsNotRetriedUnderTheOlderName() async throws {
        let transport = FakeConnect(["1/RetrieveUserQuotaSummary": .status(401, "missing CSRF token")])
        let reading = try await AntigravityLoopbackClient(transport: transport).reading(ports: [1], csrfToken: "t")
        XCTAssertNil(reading)
        XCTAssertEqual(transport.calls, ["1/RetrieveUserQuotaSummary"])
    }

    func testCancellationPropagates() async {
        let transport = FakeConnect(["1/RetrieveUserQuotaSummary": .failure(CancellationError())])
        do {
            _ = try await AntigravityLoopbackClient(transport: transport).reading(ports: [1, 2], csrfToken: "t")
            XCTFail("expected cancellation")
        } catch {
            XCTAssertTrue(error is CancellationError)
            XCTAssertEqual(transport.calls, ["1/RetrieveUserQuotaSummary"])
        }
    }

    func testTrustsSelfSignedCertificateOnlyForTheLoopbackLiteral() {
        XCTAssertTrue(AntigravityLoopbackTrust.isTrustedLoopback(URL(string: "https://127.0.0.1:6112/x")))
        for url in ["https://localhost:6112/x", "https://127.0.0.2:6112/x", "https://10.0.0.5:6112/x",
                    "https://example.com/x", "http://127.0.0.1:6112/x", "https://127.0.0.1.example.com/x"] {
            XCTAssertFalse(AntigravityLoopbackTrust.isTrustedLoopback(URL(string: url)), url)
        }
        XCTAssertFalse(AntigravityLoopbackTrust.isTrustedLoopback(nil))
    }

    func testBuildsTheConnectServiceURL() throws {
        let url = try XCTUnwrap(AntigravityLoopbackClient.requestURL(port: 6112, method: "RetrieveUserQuotaSummary"))
        XCTAssertEqual(url.scheme, "https")
        XCTAssertEqual(url.host, "127.0.0.1")
        XCTAssertEqual(url.port, 6112)
        XCTAssertEqual(url.path, "/exa.language_server_pb.LanguageServerService/RetrieveUserQuotaSummary")
        XCTAssertNil(AntigravityLoopbackClient.requestURL(port: 0, method: "X"))
        XCTAssertNil(AntigravityLoopbackClient.requestURL(port: 70000, method: "X"))
    }
}
