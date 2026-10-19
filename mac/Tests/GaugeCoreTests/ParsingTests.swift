import Foundation
import XCTest
@testable import GaugeCore

final class TimestampTests: XCTestCase {
    func testOffsetKeepsInstant() {
        let date = Timestamp.parse("2026-10-19T09:00:00+09:00")
        XCTAssertEqual(date, Date(timeIntervalSince1970: 1_792_368_000))
    }

    func testMissingOffsetIsUTC() {
        XCTAssertEqual(Timestamp.parse("2026-10-19T00:00:00"), Date(timeIntervalSince1970: 1_792_368_000))
        XCTAssertEqual(Timestamp.parse("2026-10-19"), Date(timeIntervalSince1970: 1_792_368_000))
    }

    func testMicrosecondFraction() throws {
        let date = try XCTUnwrap(Timestamp.parse("2026-10-19T00:00:00.250000Z"))
        XCTAssertEqual(date.timeIntervalSince1970, 1_792_368_000.25, accuracy: 0.0001)
    }

    func testRejectsMalformed() {
        XCTAssertNil(Timestamp.parse("2026-02-30"))
        XCTAssertNil(Timestamp.parse("not a date"))
        XCTAssertNil(Timestamp.parse("2026-10-19T25:00:00Z"))
    }
}

final class ClaudeParserTests: XCTestCase {
    func testLegacyWindowsAndScopedWeekly() throws {
        let json = """
        {
          "five_hour": { "utilization": 42.5, "resets_at": "2026-10-19T05:00:00+00:00" },
          "seven_day": { "utilization": 10, "resets_at": "2026-10-22T00:00:00Z" },
          "seven_day_opus": null,
          "limits": [
            { "kind": "session", "group": "session", "percent": 42.5 },
            { "kind": "weekly_scoped", "group": "weekly", "percent": 61, "is_active": false,
              "resets_at": "2026-10-22T00:00:00Z",
              "scope": { "model": { "id": "fable", "display_name": "Fable" } } },
            { "kind": "weekly_scoped", "group": "weekly", "percent": 5,
              "scope": { "model": { "id": "all-models", "display_name": "All models" } } }
          ]
        }
        """
        let windows = try ClaudeUsageParser.windows(from: Data(json.utf8))
        XCTAssertEqual(windows.map(\.id), ["fiveHour", "weekly", "claude-weekly-scoped-fable"])
        XCTAssertEqual(windows[0].usedRatio, 0.425, accuracy: 0.0001)
        XCTAssertEqual(windows[0].duration, 5 * 3600)
        XCTAssertEqual(windows[2].groupLabel, "Fable")
        XCTAssertEqual(windows[2].usedRatio, 0.61, accuracy: 0.0001)
    }

    func testNullUtilizationOmitsWindow() throws {
        let windows = try ClaudeUsageParser.windows(from: Data(#"{"five_hour":{"utilization":null},"seven_day":{"utilization":3}}"#.utf8))
        XCTAssertEqual(windows.map(\.type), [.weekly])
    }
}

final class CodexParserTests: XCTestCase {
    func testWeeklyOnlyPrimaryIsClassifiedByDuration() throws {
        let json = """
        { "plan_type": "plus",
          "rate_limit": {
            "primary_window": { "used_percent": 30, "reset_at": 1792368000, "limit_window_seconds": 604800 },
            "secondary_window": null } }
        """
        let payload = try CodexUsageParser.payload(from: Data(json.utf8))
        XCTAssertEqual(payload.plan, "Plus")
        XCTAssertEqual(payload.windows.map(\.type), [.weekly])
        XCTAssertEqual(payload.windows[0].duration, 604_800)
        XCTAssertNil(payload.resetCredits)
    }

    func testBothWindowsAdditionalLimitsAndCredits() throws {
        let json = """
        { "plan_type": "pro",
          "rate_limit": {
            "primary_window": { "used_percent": 12, "reset_at": 1792368000, "limit_window_seconds": 18000 },
            "secondary_window": { "used_percent": 55, "reset_at": 1792800000, "limit_window_seconds": 604800 } },
          "additional_rate_limits": [
            { "limit_name": "GPT Reserve", "metered_feature": "gpt_reserve",
              "rate_limit": { "primary_window": { "used_percent": 80, "reset_at": 1792368000, "limit_window_seconds": 18000 } } },
            { "limit_name": "", "rate_limit": {} } ],
          "rate_limit_reset_credits": { "available_count": 2, "applicable_available_count": 0 } }
        """
        let payload = try CodexUsageParser.payload(from: Data(json.utf8))
        XCTAssertEqual(payload.plan, "Pro")
        XCTAssertEqual(payload.windows.map(\.id), ["fiveHour", "weekly", "codex-additional-gpt-reserve-18000"])
        XCTAssertEqual(payload.windows[2].groupLabel, "Luna Reserve")
        XCTAssertEqual(payload.resetCredits, 2)
    }

    func testUnknownDurationIsDroppedNotReinterpreted() throws {
        let json = #"{"rate_limit":{"primary_window":{"used_percent":5,"limit_window_seconds":3600}}}"#
        XCTAssertTrue(try CodexUsageParser.payload(from: Data(json.utf8)).windows.isEmpty)
    }

    func testLegacyPositionalShapeHasNoDuration() throws {
        let json = #"{"rate_limit":{"primary_window":{"used_percent":5},"secondary_window":{"used_percent":7}}}"#
        let windows = try CodexUsageParser.payload(from: Data(json.utf8)).windows
        XCTAssertEqual(windows.map(\.type), [.fiveHour, .weekly])
        XCTAssertNil(windows[0].duration)
    }
}

final class CredentialParsingTests: XCTestCase {
    let now = Date(timeIntervalSince1970: 1_792_368_000)

    func testClaudeDocument() {
        let json = #"{"claudeAiOauth":{"accessToken":"tok","expiresAt":1892368000000,"subscriptionType":"max","rateLimitTier":"default_claude_max_20x"}}"#
        guard case .available(let credential) = CredentialParsing.claude(from: Data(json.utf8), now: now) else {
            return XCTFail("expected a credential")
        }
        XCTAssertEqual(credential.accessToken, "tok")
        XCTAssertEqual(credential.plan, "Max 20x")
    }

    func testClaudeExpiredAndMalformed() {
        let expired = #"{"claudeAiOauth":{"accessToken":"tok","expiresAt":1000}}"#
        XCTAssertEqual(CredentialParsing.claude(from: Data(expired.utf8), now: now), .invalid(.expired))
        XCTAssertEqual(CredentialParsing.claude(from: Data("{".utf8), now: now), .invalid(.unreadable))
        XCTAssertEqual(CredentialParsing.claude(from: Data("{}".utf8), now: now), .invalid(.noToken))
    }

    func testCodexJWTExpiry() {
        // Payload {"exp":1000} (already expired).
        let token = "eyJhbGciOiJub25lIn0.eyJleHAiOjEwMDB9.sig"
        XCTAssertEqual(CredentialParsing.jwtExpiry(token), Date(timeIntervalSince1970: 1000))
        let json = "{\"tokens\":{\"access_token\":\"\(token)\",\"account_id\":\"acct\"}}"
        XCTAssertEqual(CredentialParsing.codex(from: Data(json.utf8), now: now), .invalid(.expired))
        XCTAssertNil(CredentialParsing.jwtExpiry("opaque-token"))
    }

    func testPlanMapping() {
        XCTAssertEqual(CredentialParsing.claudePlan(subscriptionType: "max", rateLimitTier: "x_max_5x"), "Max 5x")
        XCTAssertEqual(CredentialParsing.claudePlan(subscriptionType: "pro", rateLimitTier: nil), "Pro")
        XCTAssertEqual(CredentialParsing.claudePlan(subscriptionType: "custom", rateLimitTier: nil), "Custom")
        XCTAssertNil(CredentialParsing.claudePlan(subscriptionType: " ", rateLimitTier: nil))
    }
}

final class PresentationTests: XCTestCase {
    func testPaceCaption() {
        let now = Date(timeIntervalSince1970: 1_000_000)
        // Half the week gone, 30% used: 20 points in reserve.
        let window = UsageWindow(type: .weekly, usedRatio: 0.30, resetTime: now.addingTimeInterval(3.5 * 86_400), duration: 7 * 86_400)
        XCTAssertEqual(PaceClassifier.caption(for: window, now: now), "+20% · 여유 있음")

        var early = window
        early.resetTime = now.addingTimeInterval(7 * 86_400 - 60)
        XCTAssertNil(PaceClassifier.caption(for: early, now: now))

        var exhausted = window
        exhausted.usedRatio = 1
        XCTAssertNil(PaceClassifier.caption(for: exhausted, now: now))
    }

    func testResetRowPhrasing() {
        let now = Date(timeIntervalSince1970: 1_000_000)
        XCTAssertEqual(ResetTimeFormatter.row(now.addingTimeInterval(2 * 3600 + 5 * 60), now: now), "2시간 5분 후 초기화")
        XCTAssertEqual(ResetTimeFormatter.row(now.addingTimeInterval(-1), now: now), "초기화됨")
        XCTAssertEqual(ResetTimeFormatter.row(nil, now: now), "")
    }

    func testGroupingPutsAccountWideFirstAndFiveHourBeforeWeekly() {
        let windows = [
            UsageWindow(type: .weekly, providerID: "fable", groupLabel: "Fable", usedRatio: 0.1),
            UsageWindow(type: .weekly, usedRatio: 0.2),
            UsageWindow(type: .fiveHour, usedRatio: 0.3),
        ]
        let groups = WindowGrouping.groups(windows)
        XCTAssertEqual(groups.map(\.label), [nil, "Fable"])
        XCTAssertEqual(groups[0].windows.map(\.type), [.fiveHour, .weekly])
    }

    func testDisplayBasis() {
        XCTAssertEqual(DisplayBasis.used.percent(for: 0.42), 42)
        XCTAssertEqual(DisplayBasis.remaining.percent(for: 0.42), 58)
        XCTAssertEqual(DisplayBasis.remaining.percent(for: 1.3), 0)
    }
}
