import Foundation
import SQLite3

/// Reads per-call token usage from Antigravity's conversation databases
/// (`~/.gemini/antigravity*/conversations/<id>.db`). Each model call is one `gen_metadata`
/// row holding an undocumented protobuf, so the field numbers below were inferred from live
/// data and may change with an app update; a row that does not decode is skipped, never
/// guessed. Observed per call (field 1 of the blob, sub-message 4): field 2 = uncached prompt
/// tokens, field 5 = prompt tokens served from cache (it equals the previous call's prompt
/// total), field 3 = output tokens including thinking (field 9 + field 10). Only
/// `gen_metadata` and step timestamps are read, never conversation content.
enum AntigravityConversationReader {
    private static let usageMessage = 4, modelField = 19, attributes = 20
    private static let uncachedPrompt = 2, output = 3, cachedPrompt = 5

    // SQLITE_TRANSIENT is a C macro Swift cannot import: SQLite copies the bound text.
    private static let transient = unsafeBitCast(-1, to: sqlite3_destructor_type.self)

    /// The conversation's own id, stable for the life of the file.
    static func identity(path: String) -> String? {
        guard let database = open(path) else { return nil }
        defer { sqlite3_close_v2(database) }
        var statement: OpaquePointer?
        guard sqlite3_prepare_v2(database, "SELECT cascade_id FROM trajectory_meta LIMIT 1", -1, &statement, nil) == SQLITE_OK else { return nil }
        defer { sqlite3_finalize(statement) }
        guard sqlite3_step(statement) == SQLITE_ROW, let text = sqlite3_column_text(statement, 0) else { return nil }
        return String(cString: text)
    }

    /// Calls with an index above `afterIdx`, oldest first, and the highest index seen. Nil
    /// when the database could not be read right now (the IDE holding it).
    static func read(path: String, afterIdx: Int64, fallback: Date) -> (records: [UsageLogRecord], lastIdx: Int64)? {
        guard let database = open(path) else { return nil }
        defer { sqlite3_close_v2(database) }
        var statement: OpaquePointer?
        guard sqlite3_prepare_v2(database, "SELECT idx, data FROM gen_metadata WHERE idx > ?1 ORDER BY idx", -1, &statement, nil) == SQLITE_OK
        else { return nil }
        defer { sqlite3_finalize(statement) }
        sqlite3_bind_int64(statement, 1, afterIdx)

        var rows: [(idx: Int64, data: Data)] = []
        while true {
            let code = sqlite3_step(statement)
            if code == SQLITE_DONE { break }
            guard code == SQLITE_ROW else {
                Log.write("cost", "Antigravity read failed: sqlite \(code)")
                return nil
            }
            let bytes = sqlite3_column_blob(statement, 1).map { Data(bytes: $0, count: Int(sqlite3_column_bytes(statement, 1))) } ?? Data()
            rows.append((sqlite3_column_int64(statement, 0), bytes))
        }

        var records: [UsageLogRecord] = []
        var last = afterIdx
        for (idx, data) in rows {
            last = idx
            guard let call = decode(data) else { continue }
            let at = call.lastStep.flatMap { stepTime(database, $0) } ?? fallback
            records.append(UsageLogRecord(timestamp: at, model: call.model, tokens: call.tokens, key: call.requestId))
        }
        return (records, last)
    }

    private struct Call {
        var model: String
        var tokens: TokenTotals
        var requestId: String?
        var lastStep: Int64?
    }

    private static func decode(_ blob: Data) -> Call? {
        let top = Proto(blob)
        guard let meta = top.message(1), let usage = meta.message(usageMessage),
              let model = meta.string(modelField), !model.isEmpty,
              let prompt = usage.varint(uncachedPrompt), let out = usage.varint(output) else { return nil }
        var requestId: String?
        var lastStep: Int64?
        for attribute in meta.messages(attributes) {
            let value = attribute.string(2)
            switch attribute.string(1) {
            case "request_id": requestId = value
            case "last_step_index": lastStep = value.flatMap { Int64($0) }
            default: break
            }
        }
        return Call(model: model,
                    tokens: TokenTotals(input: prompt, cacheRead: usage.varint(cachedPrompt) ?? 0, cacheWrite: 0, cacheWrite1h: 0, output: out),
                    requestId: requestId, lastStep: lastStep)
    }

    // A step's metadata starts with a protobuf Timestamp (seconds, nanos) of its creation.
    private static func stepTime(_ database: OpaquePointer, _ step: Int64) -> Date? {
        var statement: OpaquePointer?
        guard sqlite3_prepare_v2(database, "SELECT metadata FROM steps WHERE idx = ?1", -1, &statement, nil) == SQLITE_OK else { return nil }
        defer { sqlite3_finalize(statement) }
        sqlite3_bind_int64(statement, 1, step)
        guard sqlite3_step(statement) == SQLITE_ROW, let bytes = sqlite3_column_blob(statement, 0) else { return nil }
        let metadata = Data(bytes: bytes, count: Int(sqlite3_column_bytes(statement, 0)))
        guard let seconds = Proto(metadata).message(1)?.varint(1), seconds > 0 else { return nil }
        return Date(timeIntervalSince1970: TimeInterval(seconds))
    }

    // Read-only so the IDE's own writes are never contended; the WAL is honoured.
    private static func open(_ path: String) -> OpaquePointer? {
        var database: OpaquePointer?
        guard sqlite3_open_v2(path, &database, SQLITE_OPEN_READONLY | SQLITE_OPEN_NOMUTEX, nil) == SQLITE_OK, let database else {
            sqlite3_close_v2(database)
            return nil
        }
        sqlite3_busy_timeout(database, 250)
        return database
    }

    /// Just enough protobuf wire parsing to pick fields out of a blob.
    private struct Proto {
        private var fields: [Int: [Value]] = [:]
        private enum Value { case varint(Int64), bytes(Data) }

        init(_ data: Data) {
            let bytes = [UInt8](data)
            var i = 0
            func varint() -> UInt64? {
                var result: UInt64 = 0
                var shift: UInt64 = 0
                while i < bytes.count, shift < 64 {
                    let b = bytes[i]
                    i += 1
                    result |= UInt64(b & 0x7f) << shift
                    if b & 0x80 == 0 { return result }
                    shift += 7
                }
                return nil
            }
            while i < bytes.count {
                guard let key = varint() else { return }
                let field = Int(key >> 3)
                switch key & 7 {
                case 0:
                    guard let v = varint() else { return }
                    fields[field, default: []].append(.varint(Int64(bitPattern: v)))
                case 1: i += 8
                case 5: i += 4
                case 2:
                    guard let length = varint(), length <= UInt64(bytes.count - i) else { return }
                    fields[field, default: []].append(.bytes(Data(bytes[i..<i + Int(length)])))
                    i += Int(length)
                default:
                    // A truncated or unexpected blob keeps whatever fields decoded before it.
                    return
                }
            }
        }

        func varint(_ field: Int) -> Int64? {
            if case let .varint(value)? = fields[field]?.first { return value }
            return nil
        }

        func string(_ field: Int) -> String? {
            if case let .bytes(data)? = fields[field]?.first { return String(data: data, encoding: .utf8) }
            return nil
        }

        func message(_ field: Int) -> Proto? {
            if case let .bytes(data)? = fields[field]?.first { return Proto(data) }
            return nil
        }

        func messages(_ field: Int) -> [Proto] {
            (fields[field] ?? []).compactMap { if case let .bytes(data) = $0 { return Proto(data) } else { return nil } }
        }
    }
}
