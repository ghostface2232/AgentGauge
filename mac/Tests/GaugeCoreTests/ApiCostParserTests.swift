import Foundation
import XCTest
@testable import GaugeCore

/// The two session-log readers behind the API-cost estimate: which lines count, how the
/// token classes normalize to one shape, and the identity that de-duplicates a response
/// written more than once.
final class ApiCostParserTests: XCTestCase {
    // MARK: Claude Code

    private func claudeLine(_ usage: String, model: String = "claude-opus-5", stop: String? = "\"end_turn\"",
                            id: String = "msg_1", requestID: String? = "req_1",
                            timestamp: String = "2026-09-02T06:59:42.746Z") -> String {
        "{\"type\":\"assistant\",\"timestamp\":\"\(timestamp)\",\"sessionId\":\"s1\""
            + (requestID.map { ",\"requestId\":\"\($0)\"" } ?? "")
            + ",\"message\":{\"id\":\"\(id)\",\"model\":\"\(model)\",\"stop_reason\":\(stop ?? "null")"
            + ",\"content\":[{\"type\":\"text\",\"text\":\"hello (\\\"usage\\\")\"}],\"usage\":\(usage)}}"
    }

    func testClaudeReadsEveryTokenClassAndTheResponseIdentity() throws {
        let line = claudeLine(#"{"input_tokens":2,"cache_creation_input_tokens":30798,"cache_read_input_tokens":42801,"output_tokens":173}"#)
        let record = try XCTUnwrap(ClaudeUsageLogParser.parse(line))
        XCTAssertEqual(record.model, "claude-opus-5")
        XCTAssertEqual(record.tokens, TokenTotals(input: 2, cacheRead: 42801, cacheWrite: 30798, output: 173))
        XCTAssertEqual(record.key, "claude:msg_1:req_1")
        XCTAssertEqual(record.timestamp.timeIntervalSince1970, Timestamp.parse("2026-09-02T06:59:42.746Z")!.timeIntervalSince1970,
                       accuracy: 0.0005)
    }

    func testClaudeSplitsTheOneHourCacheSliceOutOfTheWriteCount() throws {
        let line = claudeLine(#"{"input_tokens":10,"cache_creation_input_tokens":1000,"cache_read_input_tokens":0,"output_tokens":5,"cache_creation":{"ephemeral_5m_input_tokens":400,"ephemeral_1h_input_tokens":600}}"#)
        XCTAssertEqual(try XCTUnwrap(ClaudeUsageLogParser.parse(line)).tokens,
                       TokenTotals(input: 10, cacheRead: 0, cacheWrite: 400, cacheWrite1h: 600, output: 5))
    }

    func testClaudeFallsBackToTheSessionIdentityWithoutARequestID() throws {
        let line = claudeLine(#"{"input_tokens":1,"output_tokens":1}"#, requestID: nil)
        XCTAssertEqual(try XCTUnwrap(ClaudeUsageLogParser.parse(line)).key, "claude:s1:msg_1")
    }

    func testClaudeSkipsTheProxyMessageStartEstimate() {
        // Stop reason still null, input counted, nothing out and no cache fields: the
        // response has not happened yet.
        XCTAssertNil(ClaudeUsageLogParser.parse(claudeLine(#"{"input_tokens":1200,"output_tokens":0}"#, stop: nil)))
        // The same shape with cache fields present is a real (if output-less) response.
        XCTAssertNotNil(ClaudeUsageLogParser.parse(
            claudeLine(#"{"input_tokens":1200,"cache_read_input_tokens":0,"output_tokens":0}"#, stop: nil)))
    }

    func testClaudeIgnoresLinesThatAreNotAPricedResponse() {
        let lines = [
            #"{"type":"user","timestamp":"2026-09-02T06:59:42Z","message":{"usage":{"input_tokens":1}}}"#,
            #"{"type":"assistant","timestamp":"2026-09-02T06:59:42Z","message":{"id":"m","model":"x","usage":{"input_tokens":0,"output_tokens":0}}}"#,
            #"{"type":"assistant","message":{"id":"m","model":"x","usage":{"input_tokens":5,"output_tokens":5}}}"#,
            #"{"type":"assistant","timestamp":"not a time","message":{"usage":{"input_tokens":5}}}"#,
            #"{"type":"assistant","usage" truncated"#,
            #"["type":"assistant","usage"]"#,
            "",
        ]
        for line in lines {
            XCTAssertNil(ClaudeUsageLogParser.parse(line), line)
        }
    }

    func testClaudeReadsAWrongTypedFieldAsAbsentInsteadOfDroppingTheLine() throws {
        // A numeric model and a string token count: the model reads as unknown and the count
        // as zero, exactly as a missing field would.
        let line = #"{"type":"assistant","timestamp":"2026-09-02T06:59:42Z","requestId":"r","message":{"id":"m","model":7,"usage":{"input_tokens":"5","output_tokens":9}}}"#
        let record = try XCTUnwrap(ClaudeUsageLogParser.parse(line))
        XCTAssertEqual(record.model, "unknown")
        XCTAssertEqual(record.tokens, TokenTotals(output: 9))
    }

    func testClaudeKeepsTheModelNameAsWrittenWhenItIsNotAKnownID() throws {
        // "<synthetic>" and aliases are recorded under their own name and then priced as
        // unknown, never guessed at.
        let record = try XCTUnwrap(ClaudeUsageLogParser.parse(claudeLine(#"{"input_tokens":3,"output_tokens":4}"#, model: "<synthetic>")))
        XCTAssertEqual(record.model, "<synthetic>")
    }

    // MARK: Codex

    private let ts = "2026-09-15T03:58:09.207Z"

    private func turnContext(_ model: String) -> String {
        "{\"timestamp\":\"\(ts)\",\"type\":\"turn_context\",\"payload\":{\"turn_id\":\"t\",\"model\":\"\(model)\",\"cwd\":\"/x\"}}"
    }

    private func usageRecord(_ responseID: String, _ input: Int, _ cached: Int, _ output: Int) -> String {
        "{\"timestamp\":\"\(ts)\",\"type\":\"token_usage_record\",\"payload\":{\"response_id\":\"\(responseID)\""
            + ",\"usage\":{\"input_tokens\":\(input),\"cached_input_tokens\":\(cached)"
            + ",\"cache_write_input_tokens\":0,\"output_tokens\":\(output),\"reasoning_output_tokens\":1}}}"
    }

    private func tokenCount(last: String?, total: String?) -> String {
        let parts = [last.map { "\"last_token_usage\":\($0)" }, total.map { "\"total_token_usage\":\($0)" }].compactMap { $0 }
        return "{\"timestamp\":\"\(ts)\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{"
            + parts.joined(separator: ",") + "}}}"
    }

    private func u(_ input: Int, _ cached: Int, _ output: Int) -> String {
        "{\"input_tokens\":\(input),\"cached_input_tokens\":\(cached),\"output_tokens\":\(output)}"
    }

    func testCodexUsageRecordTakesTheModelFromTheLatestTurnContext() throws {
        var parser = CodexUsageLogParser()
        XCTAssertNil(parser.feed(turnContext("gpt-5.5")))
        let record = try XCTUnwrap(parser.feed(usageRecord("resp_1", 25886, 4864, 124)))
        XCTAssertEqual(record.model, "gpt-5.5")
        // Codex counts cached tokens inside input; the record keeps the uncached share alone.
        XCTAssertEqual(record.tokens, TokenTotals(input: 25886 - 4864, cacheRead: 4864, output: 124))
        XCTAssertEqual(record.key, "codex:resp_1")
    }

    func testCodexUsesLastTokenUsageAndIgnoresAReemittedTotal() throws {
        var parser = CodexUsageLogParser()
        _ = parser.feed(turnContext("gpt-5.4"))
        let first = parser.feed(tokenCount(last: u(100, 40, 10), total: u(100, 40, 10)))
        let repeated = parser.feed(tokenCount(last: u(100, 40, 10), total: u(100, 40, 10)))
        let second = parser.feed(tokenCount(last: u(50, 0, 5), total: u(150, 40, 15)))

        XCTAssertEqual(first?.tokens, TokenTotals(input: 60, cacheRead: 40, output: 10))
        XCTAssertNil(repeated)
        XCTAssertEqual(second?.tokens, TokenTotals(input: 50, output: 5))
        XCTAssertNotNil(second)
        XCTAssertNil(second?.key)
    }

    func testCodexDerivesADeltaWhenOnlyTheRunningTotalIsPresent() {
        var parser = CodexUsageLogParser()
        _ = parser.feed(turnContext("gpt-5.4"))
        XCTAssertEqual(parser.feed(tokenCount(last: nil, total: u(100, 40, 10)))?.tokens, TokenTotals(input: 60, cacheRead: 40, output: 10))
        XCTAssertEqual(parser.feed(tokenCount(last: nil, total: u(150, 60, 15)))?.tokens, TokenTotals(input: 30, cacheRead: 20, output: 5))
        // A total below the watermark is a stale replay: it adds nothing and moves nothing.
        XCTAssertNil(parser.feed(tokenCount(last: nil, total: u(120, 50, 12))))
        XCTAssertEqual(parser.feed(tokenCount(last: nil, total: u(160, 60, 16)))?.tokens, TokenTotals(input: 10, output: 1))
    }

    func testCodexIgnoresTokenCountsOnceAFileHasShownUsageRecords() {
        // New-format files write both; the per-response record is authoritative, so the
        // same response must not be counted again from its token_count.
        var parser = CodexUsageLogParser()
        _ = parser.feed(turnContext("gpt-5.5"))
        XCTAssertNotNil(parser.feed(usageRecord("resp_1", 100, 0, 10)))
        XCTAssertNil(parser.feed(tokenCount(last: u(100, 0, 10), total: u(100, 0, 10))))
    }

    func testCodexStateCarriesTheModelAndWatermarksAcrossReads() throws {
        var parser = CodexUsageLogParser()
        _ = parser.feed(turnContext("gpt-5.3-codex"))
        _ = parser.feed(tokenCount(last: nil, total: u(100, 0, 10)))

        // A later incremental read resumes from the persisted state.
        var resumed = CodexUsageLogParser(state: parser.state)
        let record = try XCTUnwrap(resumed.feed(tokenCount(last: nil, total: u(130, 0, 13))))
        XCTAssertEqual(record.model, "gpt-5.3-codex")
        XCTAssertEqual(record.tokens, TokenTotals(input: 30, output: 3))
    }

    func testCodexStateRoundTripsThroughJSON() throws {
        var parser = CodexUsageLogParser()
        _ = parser.feed(turnContext("gpt-5.4"))
        _ = parser.feed(tokenCount(last: nil, total: u(100, 40, 10)))
        let data = try JSONEncoder().encode(parser.state)
        XCTAssertEqual(try JSONDecoder().decode(CodexUsageLogParser.State.self, from: data), parser.state)
    }

    func testCodexWithoutATurnContextRecordsAnUnknownModel() {
        var parser = CodexUsageLogParser()
        XCTAssertEqual(parser.feed(usageRecord("r", 10, 0, 1))?.model, "unknown")
    }

    func testCodexAnEmptyTurnContextModelClearsTheStaleOne() {
        var parser = CodexUsageLogParser()
        _ = parser.feed(turnContext("gpt-5.4"))
        _ = parser.feed(turnContext(""))
        XCTAssertEqual(parser.feed(usageRecord("r", 10, 0, 1))?.model, "unknown")
    }

    func testCodexIgnoresLinesThatCarryNoUsage() {
        let lines = [
            #"{"timestamp":"2026-09-15T03:58:09Z","type":"response_item","payload":{"type":"message","content":"we discussed token_count here"}}"#,
            #"{"type":"session_meta","payload":{"id":"x"}}"#,
            #"{"timestamp":"2026-09-15T03:58:09Z","type":"event_msg","payload":{"type":"token_count","info":null}}"#,
            "not json but mentions token_count",
        ]
        for line in lines {
            var parser = CodexUsageLogParser()
            XCTAssertNil(parser.feed(line), line)
        }
    }
}
