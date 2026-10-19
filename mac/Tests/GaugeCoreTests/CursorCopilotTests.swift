import Foundation
import SQLite3
import XCTest
@testable import GaugeCore

/// An unsigned JWT carrying the given claims; only the payload is ever read.
private func makeJWT(_ claims: [String: Any]) -> String {
    func encode(_ data: Data) -> String {
        data.base64EncodedString()
            .replacingOccurrences(of: "+", with: "-")
            .replacingOccurrences(of: "/", with: "_")
            .replacingOccurrences(of: "=", with: "")
    }
    let header = encode(Data(#"{"alg":"none"}"#.utf8))
    let payload = encode(try! JSONSerialization.data(withJSONObject: claims))
    return "\(header).\(payload).sig"
}

private func temporaryDirectory() throws -> URL {
    let url = FileManager.default.temporaryDirectory.appending(path: "GaugeCoreTests-\(UUID().uuidString)", directoryHint: .isDirectory)
    try FileManager.default.createDirectory(at: url, withIntermediateDirectories: true)
    return url
}

final class CursorProviderTests: XCTestCase {
    private func provider(_ transport: FakeTransport, _ credentials: FakeCredentials = FakeCredentials(
        .available(ToolCredential(accessToken: "jwt-token", accountID: "user_abc")))) -> HTTPUsageProvider {
        HTTPUsageProvider(tool: .cursor, credentials: credentials, endpoint: CursorEndpoint(),
                          policy: FetchPolicy(), transport: transport)
    }

    private func ratio(_ json: String) async throws -> Double {
        let transport = FakeTransport()
        transport.enqueue(.status(200, json))
        let windows = try await provider(transport).fetch(.background).snapshot.windows
        XCTAssertEqual(windows.count, 1)
        return try XCTUnwrap(windows.first).usedRatio
    }

    func testParsesTotalPercentIntoSingleBillingCycleWindow() async throws {
        let transport = FakeTransport()
        transport.enqueue(.status(200, """
        {
          "membershipType": "pro",
          "billingCycleEnd": "2026-07-01T00:00:00Z",
          "individualUsage": { "plan": { "used": 2000, "limit": 2000, "totalPercentUsed": 36.0 } }
        }
        """))
        let outcome = try await provider(transport).fetch(.background)

        XCTAssertTrue(outcome.isLive)
        XCTAssertEqual(outcome.snapshot.plan, "Pro")
        XCTAssertEqual(outcome.snapshot.windows.count, 1)
        let window = try XCTUnwrap(outcome.snapshot.windows.first)
        XCTAssertEqual(window.type, .billingCycle)
        XCTAssertEqual(window.usedRatio, 0.36, accuracy: 0.001)
        XCTAssertEqual(window.resetTime, Timestamp.parse("2026-07-01T00:00:00Z"))
        // The cookie is built from the user id and the token, separator URL-encoded.
        XCTAssertEqual(transport.lastRequest?.value(forHTTPHeaderField: "Cookie"),
                       "WorkosCursorSessionToken=user_abc%3A%3Ajwt-token")
    }

    func testFallsBackToUsedOverLimitWhenNoTotalPercent() async throws {
        let value = try await ratio(#"{ "individualUsage": { "plan": { "used": 1500, "limit": 2000 } } }"#)
        XCTAssertEqual(value, 0.75, accuracy: 0.001)
    }

    func testAveragesAutoAndApiWhenNoTotalPercent() async throws {
        let value = try await ratio(#"{ "individualUsage": { "plan": { "autoPercentUsed": 40, "apiPercentUsed": 60 } } }"#)
        XCTAssertEqual(value, 0.50, accuracy: 0.001)
    }

    func testFallsThroughPrecedenceChain() async throws {
        let cases: [(String, Double)] = [
            (#"{ "individualUsage": { "plan": { "apiPercentUsed": 30 } } }"#, 0.30),
            (#"{ "individualUsage": { "plan": { "autoPercentUsed": 25 } } }"#, 0.25),
            (#"{ "individualUsage": { "overall": { "used": 3, "limit": 4 } } }"#, 0.75),
            (#"{ "teamUsage": { "pooled": { "used": 1, "limit": 5 } } }"#, 0.20),
        ]
        for (json, expected) in cases {
            let value = try await ratio(json)
            XCTAssertEqual(value, expected, accuracy: 0.001, json)
        }
    }

    func testUnrecognizedShapeThrowsInsteadOfFabricatingZeroPercent() async throws {
        // Schema drift must propagate as a failure so the coordinator keeps the last good
        // snapshot — a fabricated 0% "success" would overwrite it.
        let transport = FakeTransport()
        transport.enqueue(.status(200, """
        {
          "membershipType": "pro",
          "billingCycleEnd": "2026-07-01T00:00:00Z",
          "individualUsage": { "planV2": { "creditsUsedPercent": 42.0 } }
        }
        """))
        do {
            _ = try await provider(transport).fetch(.background)
            XCTFail("expected the unrecognized shape to throw")
        } catch let error as UsageFetchError {
            XCTAssertEqual(error, .invalidResponse)
        }
    }

    func testPercentAboveHundredIsClamped() async throws {
        let value = try await ratio(#"{ "individualUsage": { "plan": { "totalPercentUsed": 250 } } }"#)
        XCTAssertEqual(value, 1.0, accuracy: 0.001)
    }

    func testMapsMembershipType() throws {
        let cases = ["pro_plus": "Pro+", "pro-plus": "Pro+", "ultra": "Ultra", "business": "Business", "hobby": "Hobby"]
        for (membership, expected) in cases {
            let json = #"{ "membershipType": "\#(membership)", "individualUsage": { "plan": { "totalPercentUsed": 10 } } }"#
            XCTAssertEqual(try CursorUsageParser.payload(from: Data(json.utf8)).plan, expected)
        }
    }

    func testNotSignedInYieldsEmptySnapshot() async throws {
        let transport = FakeTransport()
        let outcome = try await provider(transport, FakeCredentials(.missing)).fetch(.background)
        XCTAssertFalse(outcome.signedIn)
        XCTAssertTrue(outcome.snapshot.windows.isEmpty)
        XCTAssertEqual(transport.requestCount, 0)
    }

    func testUnauthorizedBecomesAuthenticationRequired() async throws {
        let transport = FakeTransport()
        transport.enqueue(.status(401, "{}"))
        do {
            _ = try await provider(transport).fetch(.background)
            XCTFail("expected an authentication failure")
        } catch let error as UsageFetchError {
            XCTAssertEqual(error, .authenticationRequired)
        }
    }
}

final class CursorCredentialTests: XCTestCase {
    private let now = Date(timeIntervalSince1970: 1_790_000_000)

    func testUserIDIsTheLastSegmentOfSub() {
        let token = makeJWT(["sub": "auth0|user_abc", "exp": 1_800_000_000])
        XCTAssertEqual(CredentialParsing.cursor(accessToken: token, now: now), .available(ToolCredential(
            accessToken: token, accountID: "user_abc", expiresAt: Date(timeIntervalSince1970: 1_800_000_000))))
    }

    func testExpiredTokenIsRefreshableExpiry() {
        let token = makeJWT(["sub": "auth0|user_abc", "exp": 1_700_000_000])
        XCTAssertEqual(CredentialParsing.cursor(accessToken: token, now: now), .invalid(.expired))
    }

    func testMissingOrMalformedTokens() {
        XCTAssertEqual(CredentialParsing.cursor(accessToken: nil, now: now), .missing)
        XCTAssertEqual(CredentialParsing.cursor(accessToken: "  ", now: now), .missing)
        XCTAssertEqual(CredentialParsing.cursor(accessToken: "not-a-jwt", now: now), .invalid(.unreadable))
        XCTAssertEqual(CredentialParsing.cursor(accessToken: makeJWT(["exp": 1_800_000_000]), now: now), .invalid(.unreadable))
        // The id goes into a cookie, so anything outside its expected alphabet is refused.
        XCTAssertEqual(CredentialParsing.cursor(accessToken: makeJWT(["sub": "auth0|user;x=1"]), now: now), .invalid(.unreadable))
    }

    func testReadsTokenFromStateDatabaseReadOnly() async throws {
        let directory = try temporaryDirectory()
        defer { try? FileManager.default.removeItem(at: directory) }
        let databaseURL = directory.appending(path: "state.vscdb")
        let token = makeJWT(["sub": "github|user_42", "exp": 1_800_000_000])
        try createItemTable(at: databaseURL, rows: ["cursorAuth/accessToken": token, "other/key": "x"])

        let source = CursorCredentialSource(databaseURL: databaseURL, now: { Date(timeIntervalSince1970: 1_790_000_000) })
        let read = await source.read(interactive: false)
        guard case .available(let credential) = read else { return XCTFail("expected a credential, got \(read)") }
        XCTAssertEqual(credential.accessToken, token)
        XCTAssertEqual(credential.accountID, "user_42")
    }

    func testDatabaseWithoutTheKeyOrFileIsMissing() async throws {
        let directory = try temporaryDirectory()
        defer { try? FileManager.default.removeItem(at: directory) }
        let databaseURL = directory.appending(path: "state.vscdb")

        let absent = await CursorCredentialSource(databaseURL: databaseURL).read(interactive: false)
        XCTAssertEqual(absent, .missing)

        try createItemTable(at: databaseURL, rows: ["other/key": "x"])
        let withoutKey = await CursorCredentialSource(databaseURL: databaseURL).read(interactive: false)
        XCTAssertEqual(withoutKey, .missing)
    }

    func testDatabaseWithoutItemTableIsUnreadable() async throws {
        let directory = try temporaryDirectory()
        defer { try? FileManager.default.removeItem(at: directory) }
        let databaseURL = directory.appending(path: "state.vscdb")
        try execute(at: databaseURL, "CREATE TABLE Unrelated (id INTEGER)")

        let read = await CursorCredentialSource(databaseURL: databaseURL).read(interactive: false)
        XCTAssertEqual(read, .invalid(.unreadable))
    }

    private func createItemTable(at url: URL, rows: [String: String]) throws {
        var statements = ["CREATE TABLE ItemTable (key TEXT UNIQUE ON CONFLICT REPLACE, value BLOB)"]
        for (key, value) in rows {
            statements.append("INSERT INTO ItemTable (key, value) VALUES ('\(key)', '\(value)')")
        }
        try execute(at: url, statements.joined(separator: ";"))
    }

    private func execute(at url: URL, _ sql: String) throws {
        var database: OpaquePointer?
        guard sqlite3_open(url.path, &database) == SQLITE_OK else { throw SQLiteReadError(code: -1) }
        defer { sqlite3_close(database) }
        let code = sqlite3_exec(database, sql, nil, nil, nil)
        guard code == SQLITE_OK else { throw SQLiteReadError(code: code) }
    }
}

// The real copilot_internal/user shape captured from a live (free-tier) account: chat and
// completions are metered, premium_interactions has no quota, monthly reset date.
private let liveFreeCopilotJSON = """
{
  "login": "octocat",
  "access_type_sku": "free_limited_copilot",
  "copilot_plan": "individual",
  "quota_snapshots": {
    "chat":        { "percent_remaining": 100.0, "unlimited": false, "has_quota": true,  "remaining": 200,  "entitlement": 200 },
    "completions": { "percent_remaining": 100.0, "unlimited": false, "has_quota": true,  "remaining": 2000, "entitlement": 2000 },
    "premium_interactions": { "percent_remaining": 0.0, "unlimited": false, "has_quota": false, "remaining": 0, "entitlement": 0 }
  },
  "quota_reset_date_utc": "2026-07-01T00:00:00Z"
}
"""

final class GitHubCopilotProviderTests: XCTestCase {
    private func provider(_ transport: FakeTransport, _ credentials: FakeCredentials = FakeCredentials(
        .available(ToolCredential(accessToken: "gho_token")))) -> HTTPUsageProvider {
        HTTPUsageProvider(tool: .githubCopilot, credentials: credentials, endpoint: GitHubCopilotEndpoint(),
                          policy: FetchPolicy(), transport: transport)
    }

    private func windows(_ json: String) throws -> [UsageWindow] {
        try GitHubCopilotUsageParser.payload(from: Data(json.utf8)).windows
    }

    func testParsesMeteredQuotasIntoBillingCycleWindows() throws {
        let payload = try GitHubCopilotUsageParser.payload(from: Data(liveFreeCopilotJSON.utf8))
        XCTAssertEqual(payload.plan, "Free")
        // chat + completions are metered; premium_interactions (has_quota=false) is skipped.
        XCTAssertEqual(payload.windows.map(\.id), ["chat", "completions"])
        XCTAssertTrue(payload.windows.allSatisfy { $0.type == .billingCycle && $0.resetTime != nil })
        XCTAssertEqual(payload.windows[0].usedRatio, 0, accuracy: 0.001)
    }

    func testBareResetDateIsReadAsUTCAndAnOffsetKeepsItsInstant() throws {
        let midnightUTC = Date(timeIntervalSince1970: 1_782_864_000) // 2026-07-01T00:00:00Z
        for raw in ["2026-07-01", "2026-07-01T09:00:00+09:00"] {
            let json = liveFreeCopilotJSON.replacingOccurrences(of: "\"2026-07-01T00:00:00Z\"", with: "\"\(raw)\"")
            // Every quota takes the account-level reset, so one distinct value is expected.
            let resets = Set(try windows(json).map(\.resetTime))
            XCTAssertEqual(resets, Set([Optional(midnightUTC)]), raw)
        }
    }

    func testEachQuotaCarriesItsOwnLabelKey() throws {
        let result = try windows(liveFreeCopilotJSON)
        XCTAssertEqual(result.map(\.labelKey), ["Label_Copilot_Chat", "Label_Copilot_Completions"])
        let labels = result.map(WindowLabels.label(for:))
        XCTAssertEqual(Set(labels).count, 2)
        XCTAssertFalse(labels.contains { $0.hasPrefix("Label_") })
    }

    func testUnknownQuotaFallsBackToItsRawIDAsKeyAndLabel() throws {
        let result = try windows(#"""
        { "quota_snapshots": { "some_future_quota": { "percent_remaining": 40.0, "has_quota": true, "unlimited": false } } }
        """#)
        let window = try XCTUnwrap(result.first)
        XCTAssertEqual(window.labelKey, "some_future_quota")
        XCTAssertEqual(WindowLabels.label(for: window), "some_future_quota")
    }

    func testComputesUsedFromPercentRemaining() throws {
        let result = try windows(#"{ "quota_snapshots": { "chat": { "percent_remaining": 25.0, "has_quota": true, "unlimited": false } } }"#)
        XCTAssertEqual(result.count, 1)
        XCTAssertEqual(result[0].usedRatio, 0.75, accuracy: 0.001)
    }

    func testFallsBackToRemainingOverEntitlementWhenNoPercent() throws {
        let result = try windows(#"""
        { "quota_snapshots": { "premium_interactions": { "has_quota": true, "unlimited": false, "remaining": 75, "entitlement": 300 } } }
        """#)
        XCTAssertEqual(result.count, 1)
        XCTAssertEqual(result[0].usedRatio, 0.75, accuracy: 0.001)
    }

    func testSkipsQuotaWithEntitlementButNoRemaining() throws {
        // A missing `remaining` is unknown, not zero: the window is skipped, never assumed spent.
        XCTAssertTrue(try windows(#"""
        { "quota_snapshots": { "premium_interactions": { "has_quota": true, "unlimited": false, "entitlement": 300 } } }
        """#).isEmpty)
    }

    func testSkipsUnlimitedAndUnmeteredQuotas() throws {
        let result = try windows(#"""
        {
          "quota_snapshots": {
            "completions": { "unlimited": true,  "has_quota": true,  "percent_remaining": 50 },
            "premium_interactions": { "unlimited": false, "has_quota": false, "percent_remaining": 50 },
            "chat": { "unlimited": false, "has_quota": true, "percent_remaining": 50 }
          }
        }
        """#)
        XCTAssertEqual(result.map(\.id), ["chat"])
    }

    func testPercentBelowZeroRemainingIsClamped() throws {
        let result = try windows(#"{ "quota_snapshots": { "chat": { "percent_remaining": -50, "has_quota": true } } }"#)
        XCTAssertEqual(result.first?.usedRatio ?? -1, 1.0, accuracy: 0.001)
    }

    func testMapsPlan() {
        XCTAssertEqual(GitHubCopilotUsageParser.plan(copilotPlan: "individual", sku: nil), "Pro")
        XCTAssertEqual(GitHubCopilotUsageParser.plan(copilotPlan: "business", sku: nil), "Business")
        XCTAssertEqual(GitHubCopilotUsageParser.plan(copilotPlan: "enterprise", sku: nil), "Enterprise")
        XCTAssertEqual(GitHubCopilotUsageParser.plan(copilotPlan: "individual", sku: "free_limited_copilot"), "Free")
    }

    func testSendsTokenAndEditorHeaders() async throws {
        let transport = FakeTransport()
        transport.enqueue(.status(200, liveFreeCopilotJSON))
        _ = try await provider(transport, FakeCredentials(.available(ToolCredential(accessToken: "gho_secret")))).fetch(.background)
        XCTAssertEqual(transport.lastAuthorization, "token gho_secret")
        XCTAssertNotNil(transport.lastRequest?.value(forHTTPHeaderField: "Editor-Version"))
    }

    func testNotSignedInYieldsEmptySnapshot() async throws {
        let outcome = try await provider(FakeTransport(), FakeCredentials(.missing)).fetch(.background)
        XCTAssertFalse(outcome.signedIn)
        XCTAssertTrue(outcome.snapshot.windows.isEmpty)
    }

    func testUnauthorizedBecomesAuthenticationRequired() async throws {
        let transport = FakeTransport()
        transport.enqueue(.status(401, "{}"))
        do {
            _ = try await provider(transport).fetch(.background)
            XCTFail("expected an authentication failure")
        } catch let error as UsageFetchError {
            XCTAssertEqual(error, .authenticationRequired)
        }
    }
}

final class GitHubCopilotCredentialTests: XCTestCase {
    struct FixedToken: GitHubTokenReading {
        let value: String?
        func token() async -> String? { value }
    }

    final class FakeLocator: CLILocating, @unchecked Sendable {
        let found: URL?
        init(_ found: URL?) { self.found = found }
        func locate(_ name: String) async -> URL? { found }
        func environment(for executable: URL) async -> [String: String] { [:] }
    }

    final class FakeRunner: ProcessRunning, @unchecked Sendable {
        let result: ProcessResult
        private(set) var arguments: [String] = []
        init(_ result: ProcessResult) { self.result = result }
        func run(_ executable: URL, arguments: [String], environment: [String: String],
                 timeout: TimeInterval, captureOutput: Bool) async -> ProcessResult {
            self.arguments = arguments
            return result
        }
    }

    func testGitHubCLITokenWins() async throws {
        let home = try temporaryDirectory()
        defer { try? FileManager.default.removeItem(at: home) }
        try writeHosts(home, #"{ "github.com": { "oauth_token": "gho_file" } }"#)
        let source = GitHubCopilotCredentialSource(tokenReader: FixedToken(value: "gho_cli"), environment: [:], homeDirectory: home)
        let read = await source.read(interactive: false)
        XCTAssertEqual(read, .available(ToolCredential(accessToken: "gho_cli")))
    }

    func testFallsBackToCopilotTokenFilePreferringGitHubDotCom() async throws {
        let home = try temporaryDirectory()
        defer { try? FileManager.default.removeItem(at: home) }
        try writeHosts(home, #"""
        { "aaa.example:Iv1.x": { "oauth_token": "gho_other" }, "github.com:Iv1.abc": { "oauth_token": "gho_file" } }
        """#)
        let source = GitHubCopilotCredentialSource(tokenReader: FixedToken(value: nil), environment: [:], homeDirectory: home)
        let read = await source.read(interactive: false)
        XCTAssertEqual(read, .available(ToolCredential(accessToken: "gho_file")))
    }

    func testNoSourceIsCleanlyMissing() async throws {
        let home = try temporaryDirectory()
        defer { try? FileManager.default.removeItem(at: home) }
        let source = GitHubCopilotCredentialSource(tokenReader: FixedToken(value: nil), environment: [:], homeDirectory: home)
        let read = await source.read(interactive: false)
        XCTAssertEqual(read, .missing)
    }

    func testCLIReaderTrimsTokenAndRequiresCleanExit() async {
        let gh = URL(fileURLWithPath: "/opt/homebrew/bin/gh")
        let runner = FakeRunner(ProcessResult(exitCode: 0, timedOut: false, launched: true, output: Data("gho_x\n".utf8)))
        let token = await GitHubCLITokenReader(locator: FakeLocator(gh), runner: runner).token()
        XCTAssertEqual(token, "gho_x")
        XCTAssertEqual(runner.arguments, ["auth", "token", "--hostname", "github.com"])

        let signedOut = FakeRunner(ProcessResult(exitCode: 1, timedOut: false, launched: true, output: Data()))
        let none = await GitHubCLITokenReader(locator: FakeLocator(gh), runner: signedOut).token()
        XCTAssertNil(none)

        let missing = await GitHubCLITokenReader(locator: FakeLocator(nil), runner: runner).token()
        XCTAssertNil(missing)
    }

    private func writeHosts(_ home: URL, _ json: String) throws {
        let directory = home.appending(path: ".config/github-copilot", directoryHint: .isDirectory)
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        try Data(json.utf8).write(to: directory.appending(path: "hosts.json"))
    }
}

final class ToolKindTests: XCTestCase {
    func testOptionalToolsAreHiddenByDefault() {
        XCTAssertEqual(ToolKind.allCases.filter(\.isShownByDefault), [.claude, .codex])
        XCTAssertEqual(ToolKind.cursor.refreshInterval, 300)
        XCTAssertEqual(ToolKind.githubCopilot.refreshInterval, 900)
    }
}
