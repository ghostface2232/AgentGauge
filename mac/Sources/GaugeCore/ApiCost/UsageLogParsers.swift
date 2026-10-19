import Foundation

// Both readers decode with Codable into small structs that name only the token, model,
// timestamp and identity fields: JSONDecoder skips everything else without building
// objects for it, which measured ~2.7x faster than JSONSerialization on real Claude Code
// lines (whose conversation content dwarfs the usage fields). Every field is read
// leniently, so an unexpected type drops that one field — the same outcome as the Windows
// reader — and never the scan.

/// Reads one assistant line of a Claude Code session log (`~/.claude/projects/**/*.jsonl`)
/// into a `UsageLogRecord`. Only the token counts, model name, timestamp and message
/// identity are read; the conversation content on the same line is never touched.
///
/// Claude Code writes a streamed message more than once, and the writes differ: the early
/// ones (stop reason still null) carry a placeholder output count of a few tokens, and only
/// the last carries the real one. Every write is returned with the same key — the message id
/// with the request id, or with the session id when a request id is absent — and the ledger
/// keeps the most complete (`ApiCostLedger.add`). A line with a null stop reason, input but
/// no output, and no cache fields at all is an API proxy's message-start estimate and is
/// skipped; Claude Code's own placeholders always carry cache fields, so they are not
/// caught by that rule and rely on the ledger instead.
public enum ClaudeUsageLogParser {
    private static let unknownModel = "unknown"
    private static let assistantMarker = Array(#""type":"assistant""#.utf8)
    private static let usageMarker = Array(#""usage""#.utf8)

    /// Cheap byte-level check so the JSON decoder only runs on candidate lines.
    public static func mayContainUsage(_ line: Data) -> Bool {
        LogBytes.contains(line, assistantMarker) && LogBytes.contains(line, usageMarker)
    }

    public static func parse(_ line: String) -> UsageLogRecord? {
        parse(Data(line.utf8), using: JSONDecoder())
    }

    public static func parse(_ line: Data, using decoder: JSONDecoder) -> UsageLogRecord? {
        guard mayContainUsage(line), let root = try? decoder.decode(ClaudeLine.self, from: line) else { return nil }
        guard root.type == "assistant", let message = root.message, let usage = message.usage,
              let timestampText = root.timestamp, let timestamp = Timestamp.parse(timestampText) else { return nil }

        var cacheWrite = usage.cacheCreation
        var cacheWrite1h: Int64 = 0
        if let creation = usage.cacheCreationDetail {
            cacheWrite1h = creation.oneHour
            // The five-minute and one-hour slices partition the cache-write count.
            cacheWrite = max(0, cacheWrite - cacheWrite1h)
        }

        let incomplete = message.stopReasonIsNull && usage.input > 0 && usage.output == 0
            && !usage.hasCacheRead && !usage.hasCacheCreation
        if incomplete { return nil }

        let tokens = TokenTotals(input: usage.input, cacheRead: usage.cacheRead, cacheWrite: cacheWrite,
                                 cacheWrite1h: cacheWrite1h, output: usage.output)
        if tokens.isZero { return nil }

        let model = message.model.flatMap { $0.isEmpty ? nil : $0 } ?? unknownModel
        return UsageLogRecord(timestamp: timestamp, model: model, tokens: tokens, key: key(root, message))
    }

    private static func key(_ root: ClaudeLine, _ message: ClaudeMessage) -> String? {
        guard let messageID = message.id, !messageID.isEmpty else { return nil }
        if let requestID = root.requestId, !requestID.isEmpty { return "claude:\(messageID):\(requestID)" }
        guard let sessionID = root.sessionId ?? root.sessionIdSnake, !sessionID.isEmpty else { return nil }
        return "claude:\(sessionID):\(messageID)"
    }
}

/// Reads a Codex rollout log (`~/.codex/sessions/**/*.jsonl`) line by line into
/// `UsageLogRecord`s. Stateful per file, because the events do not stand alone: the model
/// comes from the latest `turn_context`, and the older `token_count` events carry
/// cumulative totals whose increase is the response's consumption.
///
/// Two generations of log coexist. Recent files write a `token_usage_record` per response
/// with that response's own usage — the authoritative source, keyed by response id. Older
/// files only have `event_msg`/`token_count`, where `last_token_usage` is the response and
/// `total_token_usage` the running sum; once a file has shown a usage record its token
/// counts are ignored so the same response is never counted from both. Only the token
/// fields, model and timestamp are read; the conversation content in the same file is
/// never touched.
public struct CodexUsageLogParser {
    /// The per-file state that must survive between incremental reads (kept in the ledger).
    public struct State: Codable, Sendable, Equatable {
        public var model: String?
        public var hasUsageRecords = false
        public var totalInput: Int64 = 0
        public var totalCached: Int64 = 0
        public var totalCacheWrite: Int64 = 0
        public var totalOutput: Int64 = 0

        public init() {}
    }

    private static let unknownModel = "unknown"
    private static let markers = [#""turn_context""#, #""token_usage_record""#, #""token_count""#].map { Array($0.utf8) }

    public private(set) var state: State
    private let decoder: JSONDecoder

    public init(state: State? = nil, decoder: JSONDecoder = JSONDecoder()) {
        self.state = state ?? State()
        self.decoder = decoder
    }

    /// Cheap byte-level check so the JSON decoder only runs on candidate lines.
    public static func mayMatter(_ line: Data) -> Bool {
        markers.contains { LogBytes.contains(line, $0) }
    }

    public mutating func feed(_ line: String) -> UsageLogRecord? { feed(Data(line.utf8)) }

    /// Feeds one line; returns a record when the line completes a response's usage.
    public mutating func feed(_ line: Data) -> UsageLogRecord? {
        guard Self.mayMatter(line), let root = try? decoder.decode(CodexLine.self, from: line),
              let payload = root.payload else { return nil }
        switch root.type {
        case "turn_context":
            // An explicit empty model clears stale context; an absent one keeps it.
            if let model = payload.model { state.model = model.isEmpty ? nil : model }
            return nil
        case "token_usage_record":
            state.hasUsageRecords = true
            guard let usage = payload.usage, let at = root.date else { return nil }
            let tokens = Self.normalize(usage)
            if tokens.isZero { return nil }
            let id = payload.responseID ?? payload.turnID
            return UsageLogRecord(timestamp: at, model: state.model ?? Self.unknownModel, tokens: tokens,
                                  key: id.map { "codex:\($0)" })
        case "event_msg":
            return payload.type == "token_count" ? tokenCount(root, payload) : nil
        default:
            return nil
        }
    }

    private mutating func tokenCount(_ root: CodexLine, _ payload: CodexPayload) -> UsageLogRecord? {
        if state.hasUsageRecords { return nil }
        guard let info = payload.info, let at = root.date else { return nil }

        let total = info.total
        // A re-emitted, unchanged total is the same response again, not new usage.
        if let total, total.input == state.totalInput, total.cached == state.totalCached,
           total.cacheWrite == state.totalCacheWrite, total.output == state.totalOutput {
            return nil
        }

        let tokens: TokenTotals
        if let last = info.last {
            tokens = Self.normalize(last)
        } else if let total {
            tokens = Self.normalize(
                input: max(0, total.input - state.totalInput), cached: max(0, total.cached - state.totalCached),
                cacheWrite: max(0, total.cacheWrite - state.totalCacheWrite), output: max(0, total.output - state.totalOutput))
        } else {
            return nil
        }

        if let total {
            // Watermarks never move backwards: a snapshot below them is a stale replay.
            state.totalInput = max(state.totalInput, total.input)
            state.totalCached = max(state.totalCached, total.cached)
            state.totalCacheWrite = max(state.totalCacheWrite, total.cacheWrite)
            state.totalOutput = max(state.totalOutput, total.output)
        }
        return tokens.isZero ? nil : UsageLogRecord(timestamp: at, model: state.model ?? Self.unknownModel, tokens: tokens, key: nil)
    }

    // Codex counts the cached and newly cached shares inside input_tokens; the record keeps
    // the uncached share alone in `input` so every provider means the same thing by it.
    private static func normalize(_ usage: CodexUsage) -> TokenTotals {
        normalize(input: usage.input, cached: usage.cached, cacheWrite: usage.cacheWrite, output: usage.output)
    }

    private static func normalize(input: Int64, cached: Int64, cacheWrite: Int64, output: Int64) -> TokenTotals {
        TokenTotals(input: max(0, input - cached - cacheWrite), cacheRead: cached, cacheWrite: cacheWrite, output: output)
    }
}

// MARK: - Decoded shapes

private struct ClaudeLine: Decodable {
    var type: String?
    var timestamp: String?
    var requestId: String?
    var sessionId: String?
    var sessionIdSnake: String?
    var message: ClaudeMessage?

    enum CodingKeys: String, CodingKey {
        case type, timestamp, requestId, sessionId, message
        case sessionIdSnake = "session_id"
    }

    init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        type = c.lenientString(.type)
        timestamp = c.lenientString(.timestamp)
        requestId = c.lenientString(.requestId)
        sessionId = c.lenientString(.sessionId)
        sessionIdSnake = c.lenientString(.sessionIdSnake)
        message = c.lenientObject(ClaudeMessage.self, .message)
    }
}

private struct ClaudeMessage: Decodable {
    var id: String?
    var model: String?
    var stopReasonIsNull: Bool
    var usage: ClaudeUsage?

    enum CodingKeys: String, CodingKey {
        case id, model, usage
        case stopReason = "stop_reason"
    }

    init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        id = c.lenientString(.id)
        model = c.lenientString(.model)
        stopReasonIsNull = c.contains(.stopReason) && ((try? c.decodeNil(forKey: .stopReason)) ?? false)
        usage = c.lenientObject(ClaudeUsage.self, .usage)
    }
}

private struct ClaudeUsage: Decodable {
    var input: Int64
    var cacheCreation: Int64
    var cacheRead: Int64
    var output: Int64
    var hasCacheRead: Bool
    var hasCacheCreation: Bool
    var cacheCreationDetail: ClaudeCacheCreation?

    enum CodingKeys: String, CodingKey {
        case input = "input_tokens"
        case cacheCreation = "cache_creation_input_tokens"
        case cacheRead = "cache_read_input_tokens"
        case output = "output_tokens"
        case cacheCreationDetail = "cache_creation"
    }

    init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        input = c.lenientCount(.input)
        cacheCreation = c.lenientCount(.cacheCreation)
        cacheRead = c.lenientCount(.cacheRead)
        output = c.lenientCount(.output)
        // Presence, whatever the value: Claude Code's own writes always carry these keys.
        hasCacheRead = c.contains(.cacheRead)
        hasCacheCreation = c.contains(.cacheCreation)
        cacheCreationDetail = c.lenientObject(ClaudeCacheCreation.self, .cacheCreationDetail)
    }
}

private struct ClaudeCacheCreation: Decodable {
    var oneHour: Int64

    enum CodingKeys: String, CodingKey {
        case oneHour = "ephemeral_1h_input_tokens"
    }

    init(from decoder: Decoder) throws {
        oneHour = try decoder.container(keyedBy: CodingKeys.self).lenientCount(.oneHour)
    }
}

private struct CodexLine: Decodable {
    var type: String?
    var timestamp: String?
    var payload: CodexPayload?

    var date: Date? { timestamp.flatMap(Timestamp.parse) }

    enum CodingKeys: String, CodingKey { case type, timestamp, payload }

    init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        type = c.lenientString(.type)
        timestamp = c.lenientString(.timestamp)
        payload = c.lenientObject(CodexPayload.self, .payload)
    }
}

private struct CodexPayload: Decodable {
    var type: String?
    var model: String?
    var responseID: String?
    var turnID: String?
    var usage: CodexUsage?
    var info: CodexInfo?

    enum CodingKeys: String, CodingKey {
        case type, model, usage, info
        case responseID = "response_id"
        case turnID = "turn_id"
    }

    init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        type = c.lenientString(.type)
        model = c.lenientString(.model)
        responseID = c.lenientString(.responseID)
        turnID = c.lenientString(.turnID)
        usage = c.lenientObject(CodexUsage.self, .usage)
        info = c.lenientObject(CodexInfo.self, .info)
    }
}

private struct CodexInfo: Decodable {
    var total: CodexUsage?
    var last: CodexUsage?

    enum CodingKeys: String, CodingKey {
        case total = "total_token_usage"
        case last = "last_token_usage"
    }

    init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        total = c.lenientObject(CodexUsage.self, .total)
        last = c.lenientObject(CodexUsage.self, .last)
    }
}

private struct CodexUsage: Decodable {
    var input: Int64
    var cached: Int64
    var cacheWrite: Int64
    var output: Int64

    enum CodingKeys: String, CodingKey {
        case input = "input_tokens"
        case cachedInput = "cached_input_tokens"
        case cacheReadInput = "cache_read_input_tokens"
        case cacheWrite = "cache_write_input_tokens"
        case output = "output_tokens"
    }

    init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        input = c.lenientCount(.input)
        cached = max(c.lenientCount(.cachedInput), c.lenientCount(.cacheReadInput))
        cacheWrite = c.lenientCount(.cacheWrite)
        output = c.lenientCount(.output)
    }
}

private extension KeyedDecodingContainer {
    func lenientString(_ key: Key) -> String? {
        try? decodeIfPresent(String.self, forKey: key)
    }

    /// A non-negative whole count; absent, null, fractional or non-numeric reads as 0. Capped
    /// far above any real response (a trillion tokens), so a corrupt line cannot overflow
    /// the sums it feeds.
    func lenientCount(_ key: Key) -> Int64 {
        min(max(0, (try? decodeIfPresent(Int64.self, forKey: key)) ?? 0), TokenTotals.maximumCount)
    }

    /// The nested object, or nil when the value is absent or not an object.
    func lenientObject<T: Decodable>(_ type: T.Type, _ key: Key) -> T? {
        try? decodeIfPresent(type, forKey: key)
    }
}

/// Byte-level substring search over raw log lines.
enum LogBytes {
    static func contains(_ haystack: Data, _ needle: [UInt8]) -> Bool {
        haystack.withUnsafeBytes { bytes in
            needle.withUnsafeBytes { pattern in
                guard let base = bytes.baseAddress, let patternBase = pattern.baseAddress else { return false }
                return memmem(base, bytes.count, patternBase, pattern.count) != nil
            }
        }
    }
}
