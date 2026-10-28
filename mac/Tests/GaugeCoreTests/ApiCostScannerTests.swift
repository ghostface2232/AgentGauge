import Foundation
import os
import XCTest
@testable import GaugeCore

/// A wall clock tests can move.
final class ApiCostTestClock: @unchecked Sendable {
    private let lock = NSLock()
    private var current: Date

    init(_ date: Date) { current = date }

    var now: Date {
        get { lock.withLock { current } }
        set { lock.withLock { current = newValue } }
    }
}

func apiCostDate(_ text: String) -> Date { Timestamp.parse(text)! }

/// Pricing, the incremental scan over session logs, and the card's cost caption: rates per
/// token class and tier, unknown models kept out of the dollars but visible as a floor, a
/// response written twice counted once, resumption from the last complete line, and a
/// shrunk file rescanned without double counting.
final class ApiCostScannerTests: XCTestCase {
    private var dir: URL!
    private var clock: ApiCostTestClock!
    private let utc = TimeZone(identifier: "UTC")!

    private var claudeRoot: URL { dir.appending(path: "claude") }
    private var codexRoot: URL { dir.appending(path: "codex") }
    private var ledgerURL: URL { dir.appending(path: ApiCostScanner.ledgerFileName) }

    override func setUpWithError() throws {
        dir = FileManager.default.temporaryDirectory.appending(path: "GaugeApiCost-\(UUID().uuidString)", directoryHint: .isDirectory)
        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        // The enumerator reports /var/folders/… as /private/var/folders/… (and
        // resolvingSymlinksInPath strips /private again); ledger keys are compared against
        // the real path.
        let real = try XCTUnwrap(realpath(dir.path, nil))
        defer { free(real) }
        dir = URL(filePath: String(cString: real), directoryHint: .isDirectory)
        clock = ApiCostTestClock(apiCostDate("2026-09-18T12:00:00Z"))
    }

    override func tearDownWithError() throws {
        try? FileManager.default.removeItem(at: dir)
    }

    // MARK: Pricing

    private func d(_ text: String) -> Decimal { Decimal(string: text, locale: Locale(identifier: "en_US_POSIX"))! }

    private let million: Int64 = 1_000_000

    func testPricesEveryTokenClassAtItsOwnRate() {
        // claude-opus-5: input 5, cache read 0.5, cache write 6.25, output 25 per million;
        // the one-hour cache slice at twice the input rate.
        let cost = ApiCostPricing.cost("claude-opus-5", TokenTotals(input: million, cacheRead: million, cacheWrite: million,
                                                                    cacheWrite1h: million, output: million))
        XCTAssertEqual(cost, d("5") + d("0.5") + d("6.25") + d("10") + d("25"))
    }

    func testOpenAICacheWritesFallBackToTheInputRate() {
        // Kept under the 272K tier so only the cache-write fallback is under test.
        XCTAssertEqual(ApiCostPricing.cost("gpt-5.4", TokenTotals(cacheWrite: 100_000)), d("0.25"))
    }

    func testALongPromptIsPricedAtTheLongContextTier() {
        XCTAssertEqual(ApiCostPricing.cost("gpt-5.5", TokenTotals(input: 272_000, output: 1_000)),
                       (Decimal(272_000) * 5 + Decimal(1_000) * 30) / 1_000_000)
        XCTAssertEqual(ApiCostPricing.cost("gpt-5.5", TokenTotals(input: 272_001, output: 1_000)),
                       (Decimal(272_001) * 10 + Decimal(1_000) * 45) / 1_000_000)
    }

    func testSonnetFourFiveCarriesItsTwoHundredKPremiumButCurrentClaudeModelsDoNot() {
        // Cache reads count toward the 200K threshold, as Anthropic bills it.
        let longPrompt = TokenTotals(input: 100_000, cacheRead: 100_001, output: 1_000)
        XCTAssertEqual(ApiCostPricing.cost("claude-sonnet-4-5-20250929", longPrompt),
                       (Decimal(100_000) * 6 + Decimal(100_001) * d("0.6") + Decimal(1_000) * d("22.5")) / 1_000_000)
        XCTAssertEqual(ApiCostPricing.cost("claude-sonnet-5", longPrompt),
                       (Decimal(100_000) * 2 + Decimal(100_001) * d("0.2") + Decimal(1_000) * 10) / 1_000_000)
    }

    func testNewestModelsCarryTheirOwnCacheReadAndLongContextRates() {
        let all = TokenTotals(input: million, cacheRead: million, cacheWrite: million, cacheWrite1h: million, output: million)
        // claude-opus-5-5 reads cache at 0.05x input (0.2), not the usual 0.1x.
        XCTAssertEqual(ApiCostPricing.cost("claude-opus-5-5", all), d("4") + d("0.2") + d("5") + d("8") + d("20"))
        // gpt-6-sol: 2/10 below 272K, 2x input and 1.5x output above it.
        XCTAssertEqual(ApiCostPricing.cost("gpt-6-sol", TokenTotals(input: 272_000, output: 1_000)),
                       (Decimal(272_000) * 2 + Decimal(1_000) * 10) / 1_000_000)
        XCTAssertEqual(ApiCostPricing.cost("gpt-6-sol", TokenTotals(input: 272_001, output: 1_000)),
                       (Decimal(272_001) * 4 + Decimal(1_000) * 15) / 1_000_000)
        // gpt-6.1-sol and claude-sonnet-5-5 read cache at 0.05x input.
        XCTAssertEqual(ApiCostPricing.cost("gpt-6.1-sol", TokenTotals(input: 100_000, cacheRead: 100_000, output: 1_000)),
                       (Decimal(100_000) * 2 + Decimal(100_000) * d("0.1") + Decimal(1_000) * 10) / 1_000_000)
        XCTAssertEqual(ApiCostPricing.cost("gpt-6.1-sol", TokenTotals(input: 200_000, cacheRead: 100_000, output: 1_000)),
                       (Decimal(200_000) * 4 + Decimal(100_000) * d("0.2") + Decimal(1_000) * 15) / 1_000_000)
        XCTAssertEqual(ApiCostPricing.cost("claude-sonnet-5-5", all), d("2") + d("0.1") + d("2.5") + d("4") + d("10"))
    }

    func testHaikuFiveFivePaysFiveTimesOnceItsPromptPassesOneHundredK() {
        // The threshold counts cache reads and writes, as Anthropic bills it.
        XCTAssertEqual(ApiCostPricing.cost("claude-haiku-5-5", TokenTotals(input: 50_000, cacheRead: 50_000, output: 1_000)),
                       (Decimal(50_000) * d("0.1") + Decimal(50_000) * d("0.01") + Decimal(1_000) * d("0.5")) / 1_000_000)
        XCTAssertEqual(ApiCostPricing.cost("claude-haiku-5-5", TokenTotals(input: 50_000, cacheRead: 50_001, output: 1_000)),
                       (Decimal(50_000) * d("0.5") + Decimal(50_001) * d("0.05") + Decimal(1_000) * d("2.5")) / 1_000_000)
    }

    func testNormalizesSnapshotsButNeverGuessesAnAlias() {
        let cases: [(String, Bool)] = [
            ("claude-opus-4-5-20251101", true), ("CLAUDE-FABLE-5-1", true), (" gpt-5.5 ", true), ("gpt-6-luna", true),
            ("claude-mythos-5-1", true), ("gpt-6.1-sol", true), ("claude-haiku-5-5", true),
            ("claude-opus-4-1-20250805", true), ("claude-opus-4-20250514", true), ("claude-sonnet-4-20250514", true), ("claude-3-5-haiku-20241022", true),
            ("claude-haiku-3-5-20241022", false),
            ("gpt-5.6-cyber", true), ("gpt-5.5-pro", true), ("gpt-5.5-cyber", true), ("gpt-5.4-pro", true),
            ("gpt-5.2-pro", true), ("gpt-5.1-codex-max", true), ("gpt-5.1-codex-mini", true), ("gpt-5-pro", true), ("gpt-5-nano", true),
            ("opus", false), ("<synthetic>", false), ("codex-auto-review", false), ("unknown", false),
        ]
        for (model, priced) in cases {
            XCTAssertEqual(ApiCostPricing.rates(for: model) != nil, priced, model)
        }
        XCTAssertTrue(ApiCostPricing.isExcluded("Codex-Auto-Review"))
    }

    func testTheLongContextTierIsDecidedPerResponseNotPerDay() throws {
        // Two 200K-prompt responses on the same day sum to 400K, but neither crossed the
        // 272K threshold, so both bill at the base rate; a third, 300K response does cross.
        try writeCodex("rollout.jsonl",
            #"{"timestamp":"2026-09-15T03:00:00Z","type":"turn_context","payload":{"model":"gpt-5.5"}}"#,
            #"{"timestamp":"2026-09-15T03:00:01Z","type":"token_usage_record","payload":{"response_id":"r1","usage":{"input_tokens":200000,"output_tokens":0}}}"#,
            #"{"timestamp":"2026-09-15T04:00:01Z","type":"token_usage_record","payload":{"response_id":"r2","usage":{"input_tokens":200000,"output_tokens":0}}}"#,
            #"{"timestamp":"2026-09-15T05:00:01Z","type":"token_usage_record","payload":{"response_id":"r3","usage":{"input_tokens":300000,"output_tokens":0}}}"#)

        XCTAssertEqual(try cost(.codex), (Decimal(400_000) * 5 + Decimal(300_000) * 10) / 1_000_000)
    }

    func testProCachedInputsPayTheFullInputRate() {
        let cases: [(String, Int, Int)] = [
            ("gpt-5.5-pro", 30, 60), ("gpt-5.4-pro", 30, 60),
            ("gpt-5.2-pro", 21, 21), ("gpt-5-pro", 15, 15),
        ]
        for (model, standardRate, longRate) in cases {
            XCTAssertEqual(ApiCostPricing.cost(model, TokenTotals(input: 172_000, cacheRead: 100_000)),
                           Decimal(272_000) * Decimal(standardRate) / 1_000_000, model)
            XCTAssertEqual(ApiCostPricing.cost(model, TokenTotals(input: 172_000, cacheRead: 100_001)),
                           Decimal(272_001) * Decimal(longRate) / 1_000_000, model)
        }
    }

    func testHaikuThreeFiveSnapshotUsesItsPublishedRates() {
        XCTAssertEqual(ApiCostPricing.cost("claude-3-5-haiku-20241022",
                                          TokenTotals(input: million, cacheRead: million, cacheWrite: million, output: million)),
                       d("5.88"))
    }

    // MARK: Scanning

    func testSumsTheMonthAndCountsAResponseWrittenTwiceOnce() throws {
        try writeClaude("a.jsonl",
            claude("msg_1", "2026-09-10T10:00:00Z", input: million),
            claude("msg_1", "2026-09-10T10:00:01Z", input: million),   // the same streamed message again
            claude("msg_2", "2026-09-11T10:00:00Z", output: million),
            claude("msg_3", "2026-08-31T23:00:00Z", input: million))   // last month
        // A resumed session copies an earlier message into a new file.
        try writeClaude("b.jsonl", claude("msg_2", "2026-09-11T10:00:00Z", output: million))

        let estimate = try XCTUnwrap(try scan(.claude).estimates[.claude])
        XCTAssertEqual(estimate.costUSD, 30)
        XCTAssertEqual(estimate.pricedTokens, TokenTotals(input: million, output: million))
        XCTAssertFalse(estimate.isFloor)
        XCTAssertEqual(estimate.year, 2026)
        XCTAssertEqual(estimate.month, 9)
    }

    func testAStreamedMessageCountsItsFinalOutputNotThePlaceholder() throws {
        try writeClaude("a.jsonl",
            claudeStreamed("m1", "2026-09-10T10:00:00Z", output: 2, final: false),
            claudeStreamed("m1", "2026-09-10T10:00:01Z", output: 2, final: false),
            claudeStreamed("m1", "2026-09-10T10:00:02Z", output: million, final: true))

        let estimate = try XCTUnwrap(try scan(.claude).estimates[.claude])
        XCTAssertEqual(estimate.pricedTokens, TokenTotals(input: 2, output: million))
        XCTAssertEqual(estimate.costUSD, (Decimal(2) * 5 + Decimal(million) * 25) / 1_000_000)
        // One response, whichever write was counted.
        XCTAssertEqual(ApiCostLedger.load(from: ledgerURL).files.values.flatMap(\.days).reduce(0) { $0 + $1.requests }, 1)
    }

    func testALaterScanCorrectsAPlaceholderCountedEarlier() throws {
        // The scan lands between the placeholder and the final write.
        let path = try writeClaude("a.jsonl", claudeStreamed("m1", "2026-09-10T10:00:00Z", output: 2, final: false))
        XCTAssertEqual(try scan(.claude).estimates[.claude]?.pricedTokens.output, 2)

        try append(path, claudeStreamed("m1", "2026-09-10T10:00:02Z", output: million, final: true) + "\n")
        XCTAssertEqual(try scan(.claude).estimates[.claude]?.pricedTokens.output, million)

        // The final write arriving again (a resumed session copying it) changes nothing.
        try writeClaude("b.jsonl", claudeStreamed("m1", "2026-09-10T10:00:02Z", output: million, final: true))
        XCTAssertEqual(try scan(.claude).estimates[.claude]?.pricedTokens.output, million)
    }

    func testAnArchivedCodexSessionIsNotCountedAgain() throws {
        // Older rollouts have only token_count (no response ids), so nothing but the file's
        // own identity keeps a moved copy from being read as new usage.
        let path = try writeCodex("rollout.jsonl",
            #"{"timestamp":"2026-09-02T03:00:00Z","type":"session_meta","payload":{"id":"sess-1"}}"#,
            #"{"timestamp":"2026-09-02T03:00:01Z","type":"turn_context","payload":{"model":"gpt-5.3-codex"}}"#,
            #"{"timestamp":"2026-09-02T03:00:02Z","type":"event_msg","payload":{"type":"token_count","info":{"total_token_usage":{"input_tokens":1000000,"cached_input_tokens":0,"output_tokens":0}}}}"#)
        XCTAssertEqual(try cost(.codex), d("1.75"))

        // Codex archives the thread: the file moves, bytes and write time unchanged.
        let archived = codexRoot.appending(path: "archived_sessions/rollout.jsonl")
        try FileManager.default.createDirectory(at: archived.deletingLastPathComponent(), withIntermediateDirectories: true)
        try FileManager.default.moveItem(at: path, to: archived)

        XCTAssertEqual(try cost(.codex), d("1.75"))
        let ledger = ApiCostLedger.load(from: ledgerURL)
        XCTAssertNotNil(ledger.files[archived.path(percentEncoded: false)])
        XCTAssertNil(ledger.files[path.path(percentEncoded: false)])
    }

    func testAFileRewrittenInPlaceIsReadAgainFromTheStart() throws {
        let path = try writeClaude("a.jsonl", claude("m1", "2026-09-10T10:00:00Z", input: million))
        XCTAssertEqual(try cost(.claude), 5)

        // Same length, different opening bytes: the old prefix no longer applies. (The write
        // time is moved on explicitly — a same-length rewrite within the same millisecond is
        // indistinguishable from no change, and is not what this test is about.)
        let modified = try XCTUnwrap(try FileManager.default.attributesOfItem(atPath: path.path)[.modificationDate] as? Date)
        try Data((claude("m9", "2026-09-10T10:00:00Z", input: 2 * million) + "\n").utf8).write(to: path)
        try FileManager.default.setAttributes([.modificationDate: modified.addingTimeInterval(60)], ofItemAtPath: path.path)
        XCTAssertEqual(try cost(.claude), 10)
    }

    func testAFileThatCannotBeOpenedKeepsItsRowsUntilItCanBeReadAgain() throws {
        let path = try writeClaude("a.jsonl", claude("m1", "2026-09-10T10:00:00Z", input: million))
        XCTAssertEqual(try cost(.claude), 5)
        try append(path, claude("m2", "2026-09-11T10:00:00Z", input: million) + "\n")

        // A file that will not open right now: its identity cannot be checked, which must not
        // read as "rewritten" — the counted month stays, and the scan is not left incomplete
        // (that would make the service rescan in a tight loop).
        try FileManager.default.setAttributes([.posixPermissions: 0o000], ofItemAtPath: path.path)
        let held = try scan(.claude)
        XCTAssertEqual(held.estimates[.claude]?.costUSD, 5)
        XCTAssertTrue(held.isComplete)

        try FileManager.default.setAttributes([.posixPermissions: 0o644], ofItemAtPath: path.path)
        XCTAssertEqual(try cost(.claude), 10)
    }

    func testANewFileUnreadableOnFirstSightIsReadOnALaterScan() throws {
        let path = try writeClaude("a.jsonl", claude("m1", "2026-09-10T10:00:00Z", input: million))
        try FileManager.default.setAttributes([.posixPermissions: 0o000], ofItemAtPath: path.path)
        XCTAssertEqual(try cost(.claude), 0)
        XCTAssertNil(ApiCostLedger.load(from: ledgerURL).files[path.path(percentEncoded: false)])

        try FileManager.default.setAttributes([.posixPermissions: 0o644], ofItemAtPath: path.path)
        XCTAssertEqual(try cost(.claude), 5)
        XCTAssertNotNil(ApiCostLedger.load(from: ledgerURL).files[path.path(percentEncoded: false)]?.fingerprint)
    }

    func testABudgetLimitedScanReportsIncompleteAndTheNextResumes() throws {
        // A one-byte budget reads a single file per scan.
        try writeClaude("a.jsonl", claude("m1", "2026-09-10T10:00:00Z", input: million))
        try writeClaude("b.jsonl", claude("m2", "2026-09-11T10:00:00Z", input: million))
        let scanner = makeScanner(maxBytes: 1)

        let first = try scanner.scan(tools: [.claude])
        XCTAssertEqual(first.estimates[.claude]?.costUSD, 5)
        XCTAssertFalse(first.isComplete)
        XCTAssertEqual(try scanner.scan(tools: [.claude]).estimates[.claude]?.costUSD, 10)
        // The second file was read; nothing is left, which only a further scan can confirm.
        let last = try scanner.scan(tools: [.claude])
        XCTAssertTrue(last.isComplete)
        XCTAssertEqual(last.estimates[.claude]?.costUSD, 10)
    }

    func testTheBudgetStopsPartwayThroughALargeFileAndTheNextScanResumes() throws {
        // One file larger than the budget: the scan stops after its first line rather than
        // reading the whole file past the budget, and the next one picks up the rest.
        try writeClaude("a.jsonl",
            claude("m1", "2026-09-10T10:00:00Z", input: million),
            claude("m2", "2026-09-11T10:00:00Z", input: million))
        let scanner = makeScanner(maxBytes: 1)

        let first = try scanner.scan(tools: [.claude])
        XCTAssertEqual(first.estimates[.claude]?.costUSD, 5)
        XCTAssertFalse(first.isComplete)
        let second = try scanner.scan(tools: [.claude])
        XCTAssertEqual(second.estimates[.claude]?.costUSD, 10)
        XCTAssertTrue(second.isComplete)
    }

    func testACancelledScanStopsWithoutSavingAndLeavesNothingHalfCounted() throws {
        try writeClaude("a.jsonl", claude("m1", "2026-09-10T10:00:00Z", input: million))
        XCTAssertThrowsError(try makeScanner().scan(tools: [.claude], isCancelled: { true })) { error in
            XCTAssertTrue(error is CancellationError)
        }
        XCTAssertFalse(FileManager.default.fileExists(atPath: ledgerURL.path))
        XCTAssertEqual(try cost(.claude), 5)
    }

    func testPruningDropsOldKeysAndEntriesOfGoneFiles() throws {
        let path = try writeClaude("a.jsonl", claude("m1", "2026-08-01T10:00:00Z", input: million))
        clock.now = apiCostDate("2026-08-02T00:00:00Z")
        _ = try scan(.claude)
        XCTAssertFalse(ApiCostLedger.load(from: ledgerURL).keys.isEmpty)

        // Two and a half months later the day, its key, and — once the file is gone — the
        // file's entry are all dropped; nothing old accumulates.
        try FileManager.default.removeItem(at: path)
        clock.now = apiCostDate("2026-10-15T00:00:00Z")
        _ = try scan(.claude)
        let ledger = ApiCostLedger.load(from: ledgerURL)
        XCTAssertTrue(ledger.keys.isEmpty)
        XCTAssertTrue(ledger.files.isEmpty)
    }

    func testOddlyShapedLinesAndLedgersNeverStopTheScan() throws {
        try writeClaude("a.jsonl",
            #"{"type":"assistant","timestamp":123,"message":{"id":"x","model":7,"usage":{"input_tokens":5}}}"#,
            #"{"type":["assistant"],"usage":1}"#,
            claude("m1", "2026-09-10T10:00:00Z", input: million))
        try Data(#"{"version":\#(ApiCostLedger.currentVersion),"files":null,"keys":null}"#.utf8).write(to: ledgerURL)
        XCTAssertEqual(try cost(.claude), 5)

        try Data(#"{"version":\#(ApiCostLedger.currentVersion),"files":{"/x.jsonl":{"tool":"claude","days":null}},"keys":{"k":null}}"#.utf8)
            .write(to: ledgerURL)
        XCTAssertEqual(try cost(.claude), 5)
    }

    func testUnknownModelsCountTokensButNoDollars() throws {
        try writeClaude("a.jsonl",
            claude("m1", "2026-09-10T10:00:00Z", input: million),
            claude("m2", "2026-09-10T11:00:00Z", input: 700, model: "<synthetic>"))

        let estimate = try XCTUnwrap(try scan(.claude).estimates[.claude])
        XCTAssertEqual(estimate.costUSD, 5)
        XCTAssertEqual(estimate.unpricedTokens, 700)
        XCTAssertEqual(estimate.unpricedModels, ["<synthetic>"])
        XCTAssertTrue(estimate.isFloor)
    }

    func testCodexAutoReviewIsLeftOutWithoutMakingTheTotalAFloor() throws {
        try writeCodex("rollout.jsonl",
            #"{"timestamp":"2026-09-15T03:00:00Z","type":"turn_context","payload":{"model":"gpt-5.3-codex"}}"#,
            #"{"timestamp":"2026-09-15T03:00:01Z","type":"token_usage_record","payload":{"response_id":"r1","usage":{"input_tokens":1000000,"output_tokens":0}}}"#,
            #"{"timestamp":"2026-09-15T03:01:00Z","type":"turn_context","payload":{"model":"codex-auto-review"}}"#,
            #"{"timestamp":"2026-09-15T03:01:01Z","type":"token_usage_record","payload":{"response_id":"r2","usage":{"input_tokens":5000000,"output_tokens":9000}}}"#)

        let estimate = try XCTUnwrap(try scan(.codex).estimates[.codex])
        XCTAssertEqual(estimate.costUSD, d("1.75"))
        XCTAssertFalse(estimate.isFloor)
        XCTAssertTrue(estimate.unpricedModels.isEmpty)
        // Still recorded, so including it later needs no rescan.
        XCTAssertTrue(ApiCostLedger.load(from: ledgerURL).files.values.flatMap(\.days).contains { $0.model == "codex-auto-review" })
    }

    func testAnAppendedFileIsReadFromWhereTheLastScanStopped() throws {
        let path = try writeClaude("a.jsonl", claude("m1", "2026-09-10T10:00:00Z", input: million))
        XCTAssertEqual(try cost(.claude), 5)

        // A complete line plus one still being written (no newline yet).
        try append(path, claude("m2", "2026-09-12T10:00:00Z", input: million) + "\n"
            + claude("m3", "2026-09-12T11:00:00Z", input: million))
        XCTAssertEqual(try cost(.claude), 10)

        // The partial line completes: it is read once, and nothing before it again.
        try append(path, "\n")
        XCTAssertEqual(try cost(.claude), 15)
        XCTAssertEqual(try cost(.claude), 15)
    }

    func testLinesLongerThanAReadChunkAreJoinedAcrossChunks() throws {
        // A Claude Code line carrying a large tool result spans several reads.
        let padding = String(repeating: "x", count: ApiCostScanner.readChunkBytes * 2 + 17)
        let long = claude("m1", "2026-09-10T10:00:00Z", input: million)
            .replacingOccurrences(of: "\"stop_reason\"", with: "\"content\":\"\(padding)\",\"stop_reason\"")
        try writeClaude("a.jsonl", long, claude("m2", "2026-09-10T11:00:00Z", input: million))
        XCTAssertEqual(try cost(.claude), 10)
    }

    func testAShrunkFileIsForgottenAndReadAgainWithoutDoubleCounting() throws {
        let path = try writeClaude("a.jsonl",
            claude("m1", "2026-09-10T10:00:00Z", input: million),
            claude("m2", "2026-09-10T11:00:00Z", input: million))
        XCTAssertEqual(try cost(.claude), 10)

        try Data((claude("m1", "2026-09-10T10:00:00Z", input: million) + "\n").utf8).write(to: path)
        XCTAssertEqual(try cost(.claude), 5)
    }

    func testCodexResumesItsPerFileStateAcrossScans() throws {
        // gpt-5.3-codex has no long-context tier, so a million-token total prices flat.
        let path = try writeCodex("rollout.jsonl",
            #"{"timestamp":"2026-09-15T03:00:00Z","type":"turn_context","payload":{"model":"gpt-5.3-codex"}}"#,
            #"{"timestamp":"2026-09-15T03:00:01Z","type":"event_msg","payload":{"type":"token_count","info":{"total_token_usage":{"input_tokens":1000000,"cached_input_tokens":0,"output_tokens":0}}}}"#)
        XCTAssertEqual(try cost(.codex), d("1.75"))

        // Only the running total is written: the next response is its increase, still priced
        // as gpt-5.3-codex from the turn context read in the previous scan.
        try append(path, #"{"timestamp":"2026-09-15T04:00:00Z","type":"event_msg","payload":{"type":"token_count","info":{"total_token_usage":{"input_tokens":2000000,"cached_input_tokens":0,"output_tokens":0}}}}"# + "\n")
        XCTAssertEqual(try cost(.codex), d("3.5"))
    }

    func testOnlyTheRequestedToolsAreRead() throws {
        try writeClaude("a.jsonl", claude("m1", "2026-09-10T10:00:00Z", input: million))
        try writeCodex("r.jsonl",
            #"{"timestamp":"2026-09-15T03:00:00Z","type":"turn_context","payload":{"model":"gpt-5.4"}}"#,
            #"{"timestamp":"2026-09-15T03:00:01Z","type":"token_usage_record","payload":{"response_id":"r1","usage":{"input_tokens":1000000,"output_tokens":0}}}"#)

        let estimates = try scan(.codex, .cursor).estimates
        XCTAssertEqual(Array(estimates.keys), [.codex])
        XCTAssertFalse(ApiCostLedger.load(from: ledgerURL).files.values.contains { $0.tool == ToolKind.claude.rawValue })
    }

    func testDaysFollowTheGivenZone() throws {
        // 23:30 UTC on 31 August is already 1 September in Seoul.
        try writeClaude("a.jsonl", claude("m1", "2026-08-31T23:30:00Z", input: million))
        let seoul = makeScanner(timeZone: TimeZone(identifier: "Asia/Seoul")!)
        XCTAssertEqual(try seoul.scan(tools: [.claude]).estimates[.claude]?.costUSD, 5)
        try FileManager.default.removeItem(at: ledgerURL)
        XCTAssertEqual(try cost(.claude), 0)
    }

    func testAnUnreadableLedgerIsRebuiltFromTheLogs() throws {
        try writeClaude("a.jsonl", claude("m1", "2026-09-10T10:00:00Z", input: million))
        try Data("{ not json".utf8).write(to: ledgerURL)
        XCTAssertEqual(try cost(.claude), 5)
    }

    func testALedgerFromAnotherVersionIsRebuilt() throws {
        try writeClaude("a.jsonl", claude("m1", "2026-09-10T10:00:00Z", input: million))
        _ = try scan(.claude)
        let stale = ApiCostLedger.load(from: ledgerURL)
        // Doubled rows a rebuild must not keep.
        for file in stale.files.values { for row in file.days { row.add(row.tokens, requests: 1) } }
        stale.version = ApiCostLedger.currentVersion - 1
        stale.save(to: ledgerURL)
        XCTAssertEqual(try cost(.claude), 5)
    }

    func testMissingLogRootsYieldAnEmptyEstimate() throws {
        XCTAssertEqual(try scan(.claude).estimates[.claude]?.isEmpty, true)
    }

    func testDefaultSourcesHonourTheConfiguredHomes() {
        let home = URL(filePath: "/Users/someone", directoryHint: .isDirectory)
        let plain = ApiCostScanner.defaultSources(home: home, environment: [:])
        XCTAssertEqual(plain.first { $0.tool == .claude }?.roots.map { $0.path(percentEncoded: false) },
                       ["/Users/someone/.claude/projects", "/Users/someone/.config/claude/projects"])
        XCTAssertEqual(plain.first { $0.tool == .codex }?.roots.map { $0.path(percentEncoded: false) },
                       ["/Users/someone/.codex/sessions", "/Users/someone/.codex/archived_sessions"])

        let configured = ApiCostScanner.defaultSources(home: home, environment: ["CLAUDE_CONFIG_DIR": "/cfg", "CODEX_HOME": "/cx"])
        XCTAssertEqual(configured.first { $0.tool == .claude }?.roots.map { $0.path(percentEncoded: false) }, ["/cfg/projects"])
        XCTAssertEqual(configured.first { $0.tool == .codex }?.roots.map { $0.path(percentEncoded: false) },
                       ["/cx/sessions", "/cx/archived_sessions"])
    }

    // MARK: Card caption

    private func estimate(_ cost: String, tokens: TokenTotals = TokenTotals(input: 1), unpriced: Int64 = 0,
                          models: [String] = []) -> ApiCostEstimate {
        ApiCostEstimate(tool: .claude, year: 2026, month: 9, costUSD: d(cost), pricedTokens: tokens,
                        unpricedTokens: unpriced, unpricedModels: models, scannedAt: clock.now)
    }

    func testCardShowsTheEstimateAndMarksAFloor() {
        let base = estimate("1234.5", tokens: TokenTotals(input: 1, cacheRead: 2, cacheWrite: 3, cacheWrite1h: 4, output: 5))
        XCTAssertEqual(ApiCostText.chip(base), "≈ $1,235")
        let tooltip = ApiCostText.tooltip(base)
        XCTAssertTrue(tooltip.contains("API"))
                XCTAssertTrue(tooltip.contains("입력 1"))
        XCTAssertTrue(tooltip.contains("캐시 쓰기 7"))
        // VoiceOver hears the amount first, then the explanation.
        XCTAssertTrue(ApiCostText.accessibilityLabel(base)?.hasPrefix("≈ $1,235. ") == true)

        let floor = estimate("1234.5", unpriced: 42, models: ["<synthetic>"])
        XCTAssertEqual(ApiCostText.chip(floor), "≈ $1,235+")
        XCTAssertTrue(ApiCostText.tooltip(floor).contains("<synthetic>"))

        XCTAssertNil(ApiCostText.chip(estimate("0", tokens: .zero)))
    }

    func testCardDropsCentsFromOneHundredDollars() {
        let cases = [("12.345", "≈ $12.35"), ("99.99", "≈ $99.99"), ("99.996", "≈ $100"), ("100", "≈ $100"),
                     ("340.5", "≈ $341"), ("3", "≈ $3.00"), ("0.004", "≈ $0.00"), ("1234567.5", "≈ $1,234,568")]
        for (cost, expected) in cases {
            XCTAssertEqual(ApiCostText.chip(estimate(cost)), expected, cost)
        }
    }

    func testTokenCountsFollowTheUILanguageButDollarsDoNot() {
        let original = L10n.language
        defer { L10n.configure(original) }
        L10n.configure(.english)
        let big = estimate("1234.5", tokens: TokenTotals(input: 1_234_567))
        XCTAssertEqual(ApiCostText.chip(big), "≈ $1,235")
        XCTAssertTrue(ApiCostText.tooltip(big).contains("Input 1,234,567"))
        XCTAssertTrue(ApiCostText.tooltip(big).contains("API-price equivalent"))
    }

    // MARK: Helpers

    private func makeScanner(maxBytes: Int64 = ApiCostScanner.defaultMaxBytesPerScan, timeZone: TimeZone? = nil) -> ApiCostScanner {
        let clock = clock!
        return ApiCostScanner(ledgerDirectory: dir, sources: sources, timeZone: timeZone ?? utc, maxBytesPerScan: maxBytes,
                              now: { clock.now })
    }

    private var sources: [ApiCostScanner.LogSource] {
        [ApiCostScanner.LogSource(tool: .claude, kind: .claude, roots: [claudeRoot]),
         ApiCostScanner.LogSource(tool: .codex, kind: .codex, roots: [codexRoot])]
    }

    private func scan(_ tools: ToolKind...) throws -> ApiCostScanner.Result {
        try makeScanner().scan(tools: Set(tools))
    }

    private func cost(_ tool: ToolKind) throws -> Decimal? {
        try scan(tool).estimates[tool]?.costUSD
    }

    private func claude(_ id: String, _ timestamp: String, input: Int64 = 0, output: Int64 = 0, model: String = "claude-opus-5") -> String {
        "{\"type\":\"assistant\",\"timestamp\":\"\(timestamp)\",\"requestId\":\"req_\(id)\",\"sessionId\":\"s\","
            + "\"message\":{\"id\":\"\(id)\",\"model\":\"\(model)\",\"stop_reason\":\"end_turn\","
            + "\"usage\":{\"input_tokens\":\(input),\"cache_read_input_tokens\":0,\"cache_creation_input_tokens\":0,\"output_tokens\":\(output)}}}"
    }

    // Claude Code's real streaming pattern: early writes carry a placeholder output count and
    // a null stop reason (with cache fields, so the message-start rule does not catch them);
    // the last write carries the real output.
    private func claudeStreamed(_ id: String, _ timestamp: String, output: Int64, final: Bool) -> String {
        "{\"type\":\"assistant\",\"timestamp\":\"\(timestamp)\",\"requestId\":\"req_\(id)\",\"sessionId\":\"s\","
            + "\"message\":{\"id\":\"\(id)\",\"model\":\"claude-opus-5\",\"stop_reason\":\(final ? "\"tool_use\"" : "null"),"
            + "\"usage\":{\"input_tokens\":2,\"cache_read_input_tokens\":0,\"cache_creation_input_tokens\":0,\"output_tokens\":\(output)}}}"
    }

    @discardableResult
    private func writeClaude(_ name: String, _ lines: String...) throws -> URL { try write(claudeRoot, name, lines) }

    @discardableResult
    private func writeCodex(_ name: String, _ lines: String...) throws -> URL { try write(codexRoot, name, lines) }

    private func write(_ root: URL, _ name: String, _ lines: [String]) throws -> URL {
        let folder = root.appending(path: "project", directoryHint: .isDirectory)
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        let url = folder.appending(path: name)
        try Data((lines.joined(separator: "\n") + "\n").utf8).write(to: url)
        return url
    }

    private func append(_ url: URL, _ text: String) throws {
        let handle = try FileHandle(forWritingTo: url)
        defer { try? handle.close() }
        try handle.seekToEnd()
        try handle.write(contentsOf: Data(text.utf8))
    }
}
