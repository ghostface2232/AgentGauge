import Foundation
import XCTest
@testable import GaugeCore

/// Tolerance and normalization for `AntigravityQuotaParser`, ported from the Windows suite.
/// `liveCapture` is a real `RetrieveUserQuotaSummary` response (identifiers aside); the other
/// cases cover the schema drift the parser must survive without inventing usage values.
final class AntigravityParserTests: XCTestCase {
    private let liveCapture = """
    {
      "response": {
        "groups": [
          {
            "displayName": "Gemini Models",
            "description": "Models within this group: Gemini Flash, Gemini Pro",
            "buckets": [
              { "bucketId": "gemini-weekly", "displayName": "Weekly Limit", "window": "weekly",
                "remainingFraction": 0.9989373, "resetTime": "2026-06-28T00:03:00Z" },
              { "bucketId": "gemini-5h", "displayName": "Five Hour Limit", "window": "5h",
                "remainingFraction": 0.9936241, "resetTime": "2026-06-21T05:03:00Z" }
            ]
          },
          {
            "displayName": "Claude and GPT models",
            "buckets": [
              { "bucketId": "3p-weekly", "window": "weekly", "remainingFraction": 1, "resetTime": "2026-06-28T00:05:32Z" },
              { "bucketId": "3p-5h", "window": "5h", "remainingFraction": 1, "resetTime": "2026-06-21T05:05:32Z" }
            ]
          }
        ],
        "description": "Within each group, models share a weekly limit and a 5-hour limit."
      }
    }
    """

    private func parse(_ json: String) throws -> [UsageWindow] {
        try AntigravityQuotaParser.windows(from: Data(json.utf8))
    }

    private func wrap(_ bucket: String) -> String {
        #"{ "response": { "groups": [ { "buckets": [ "# + bucket + " ] } ] } }"
    }

    func testParsesFourWindowsFromLiveCapture() throws {
        let windows = try parse(liveCapture)

        // Provider order preserved; each bucketId is the stable id, keeping the two 5-hour
        // windows apart.
        XCTAssertEqual(windows.map(\.id), ["gemini-weekly", "gemini-5h", "3p-weekly", "3p-5h"])
        XCTAssertEqual(windows.map(\.groupLabel), ["Gemini", "Gemini", "Claude/GPT", "Claude/GPT"])

        XCTAssertEqual(windows[0].type, .weekly)
        XCTAssertEqual(windows[0].usedRatio, 1 - 0.9989373, accuracy: 1e-6)
        XCTAssertEqual(windows[0].resetTime, Timestamp.parse("2026-06-28T00:03:00Z"))
        XCTAssertEqual(windows[0].duration, 7 * 86400)

        XCTAssertEqual(windows[1].type, .fiveHour)
        XCTAssertEqual(windows[1].usedRatio, 1 - 0.9936241, accuracy: 1e-6)
        XCTAssertEqual(windows[1].duration, 5 * 3600)

        // remainingFraction == 1 → fully available, never clamped into spent.
        XCTAssertEqual(windows[2].usedRatio, 0, accuracy: 1e-9)
        XCTAssertEqual(windows[3].usedRatio, 0, accuracy: 1e-9)
    }

    /// Observed live in October 2026: a model-scoped group beside the two shared families.
    /// It keeps its own name rather than collapsing into a second "Gemini" weekly window.
    func testModelScopedGroupKeepsItsOwnName() throws {
        let windows = try parse("""
        { "response": { "groups": [
          { "displayName": "Gemini Models", "buckets": [
            { "bucketId": "gemini-weekly", "window": "weekly", "remainingFraction": 1 } ] },
          { "displayName": "Gemini 4 Argon", "buckets": [
            { "bucketId": "gemini-argon-wtu-weekly", "window": "weekly", "remainingFraction": 0.844542 } ] }
        ] } }
        """)
        XCTAssertEqual(windows.map(\.groupLabel), ["Gemini", "Gemini 4 Argon"])
        XCTAssertEqual(Set(windows.map(\.id)).count, 2)
    }

    func testFamilyLabelFallsBackToBucketPrefixWithoutGroupName() {
        XCTAssertEqual(AntigravityQuotaParser.familyLabel(bucketID: "gemini-5h", groupName: nil), "Gemini")
        XCTAssertEqual(AntigravityQuotaParser.familyLabel(bucketID: "3p-weekly", groupName: "  "), "Claude/GPT")
        XCTAssertNil(AntigravityQuotaParser.familyLabel(bucketID: "other", groupName: nil))
    }

    func testUsesGroupMetadataWhenBucketIdsChange() throws {
        let window = try XCTUnwrap(parse("""
        { "response": { "groups": [{ "displayName": "Claude and GPT models",
          "buckets": [{ "bucketId": "partner-weekly-v2", "window": "weekly", "remainingFraction": 0.8 }] }] } }
        """).first)
        XCTAssertEqual(window.groupLabel, "Claude/GPT")
    }

    func testToleratesSummaryAliasAndRootLevelGroups() throws {
        for json in [
            #"{ "summary": { "groups": [ { "buckets": [ { "bucketId": "gemini-5h", "window": "5h", "remainingFraction": 0.5 } ] } ] } }"#,
            #"{ "groups": [ { "buckets": [ { "bucketId": "gemini-5h", "window": "5h", "remainingFraction": 0.5 } ] } ] }"#,
        ] {
            let windows = try parse(json)
            XCTAssertEqual(windows.count, 1)
            XCTAssertEqual(windows.first?.id, "gemini-5h")
            XCTAssertEqual(windows.first?.usedRatio ?? -1, 0.5, accuracy: 1e-9)
        }
    }

    func testResolvesAndClampsRemainingFraction() throws {
        let cases: [(String, Double)] = [
            (#"{ "bucketId": "b", "window": "5h", "remaining": { "case": "remainingFraction", "value": 0.25 } }"#, 0.75),
            (#"{ "bucketId": "b", "window": "5h", "remaining": { "remainingFraction": 0.25 } }"#, 0.75),
            (#"{ "bucketId": "b", "window": "5h", "remainingFraction": -0.5 }"#, 1),
            (#"{ "bucketId": "b", "window": "5h", "remainingFraction": 1.5 }"#, 0),
        ]
        for (bucket, expected) in cases {
            let windows = try parse(wrap(bucket))
            XCTAssertEqual(windows.count, 1, bucket)
            XCTAssertEqual(windows.first?.usedRatio ?? -1, expected, accuracy: 1e-9, bucket)
        }
    }

    func testParsesEpochSecondsResetTimeAsFallback() throws {
        let window = try XCTUnwrap(parse(wrap(#"{ "bucketId": "b", "window": "weekly", "remainingFraction": 0.5, "resetTime": 1790000000 }"#)).first)
        XCTAssertEqual(window.resetTime, Date(timeIntervalSince1970: 1_790_000_000))
    }

    func testSkipsUnusableBuckets() throws {
        for bucket in [
            // No usable fraction → skipped, never assumed spent.
            #"{ "bucketId": "b", "window": "5h", "resetTime": "2026-06-21T05:03:00Z" }"#,
            #"{ "bucketId": "b", "window": "5h", "remainingFraction": 0.5, "disabled": true }"#,
            // Missing / blank bucketId → cannot be reconciled.
            #"{ "window": "5h", "remainingFraction": 0.5 }"#,
            #"{ "bucketId": "   ", "window": "5h", "remainingFraction": 0.5 }"#,
            // Unrecognized or absent window → cannot be classified.
            #"{ "bucketId": "b", "window": "monthly", "remainingFraction": 0.5 }"#,
            #"{ "bucketId": "b", "remainingFraction": 0.5 }"#,
            // A boolean is not a fraction.
            #"{ "bucketId": "b", "window": "5h", "remainingFraction": true }"#,
        ] {
            XCTAssertTrue(try parse(wrap(bucket)).isEmpty, bucket)
        }
    }

    func testIgnoresUnknownFieldsAndUnparseableGroups() throws {
        let windows = try parse("""
        { "futureTopLevel": 7, "response": { "extra": "ignored", "groups": [
          { "displayName": "Future family", "buckets": "not-an-array" },
          { "buckets": [ { "bucketId": "gemini-5h", "window": "5h", "remainingFraction": 0.5, "unknown": true } ] } ] } }
        """)
        XCTAssertEqual(windows.map(\.id), ["gemini-5h"])
    }

    func testWithdrawnGroupOrBucketIsOmitted() throws {
        for state in [#""disabled": true"#, #""isDisabled": true"#, #""enabled": false"#, #""available": false"#] {
            let onBucket = """
            { "response": { "groups": [{ "displayName": "Claude and GPT models", "buckets": [{
              "bucketId": "3p-weekly", "window": "weekly", "remainingFraction": 0.8, \(state) }] }] } }
            """
            let onGroup = """
            { "response": { "groups": [{ "displayName": "Claude and GPT models", \(state), "buckets": [{
              "bucketId": "3p-weekly", "window": "weekly", "remainingFraction": 0.8 }] }] } }
            """
            XCTAssertTrue(try parse(onBucket).isEmpty, state)
            XCTAssertTrue(try parse(onGroup).isEmpty, state)
        }
    }

    func testReturnsEmptyWhenNoBuckets() throws {
        for json in ["{}", #"{ "response": {} }"#, #"{ "response": { "groups": [] } }"#] {
            XCTAssertTrue(try parse(json).isEmpty, json)
        }
    }

    func testMalformedJSONThrows() {
        XCTAssertThrowsError(try parse("{ not json"))
    }

    func testParsesPlanFromUserStatus() {
        let cases: [(String, String?)] = [
            (#"{ "userStatus": { "planStatus": { "planInfo": { "planName": "Pro" } } } }"#, "Pro"),
            (#"{ "userStatus": { "userTier": { "name": "Google AI Pro" } } }"#, "Google AI Pro"),
            (#"{ "userStatus": { "planStatus": { "planInfo": { "planName": "Pro" } }, "userTier": { "name": "Google AI Pro" } } }"#, "Pro"),
            (#"{ "response": { "userStatus": { "planStatus": { "planInfo": { "planName": " Ultra " } } } } }"#, "Ultra"),
            (#"{ "userStatus": { } }"#, nil),
            ("{ }", nil),
            ("not json", nil),
        ]
        for (json, expected) in cases {
            XCTAssertEqual(AntigravityUserStatus.plan(from: Data(json.utf8)), expected, json)
        }
    }
}
