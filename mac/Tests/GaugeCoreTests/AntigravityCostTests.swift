import Foundation
import SQLite3
import XCTest
@testable import GaugeCore

/// The Antigravity conversation reader against a fixture database built with the same table
/// layout and protobuf shape as the live ones.
final class AntigravityCostTests: XCTestCase {
    private var dir: URL!
    private let utc = TimeZone(identifier: "UTC")!
    private var dbPath: String { dir.appending(path: "conversations/c1.db").path(percentEncoded: false) }

    override func setUpWithError() throws {
        dir = FileManager.default.temporaryDirectory.appending(path: "GaugeAgy-\(UUID().uuidString)", directoryHint: .isDirectory)
        try FileManager.default.createDirectory(at: dir.appending(path: "conversations"), withIntermediateDirectories: true)
        let real = try XCTUnwrap(realpath(dir.path, nil))
        defer { free(real) }
        dir = URL(filePath: String(cString: real), directoryHint: .isDirectory)
    }

    override func tearDownWithError() throws { try? FileManager.default.removeItem(at: dir) }

    // MARK: Fixture encoding

    private func varint(_ value: UInt64) -> [UInt8] {
        var v = value, out: [UInt8] = []
        repeat {
            var b = UInt8(v & 0x7f)
            v >>= 7
            if v != 0 { b |= 0x80 }
            out.append(b)
        } while v != 0
        return out
    }

    private func field(_ number: Int, varint value: UInt64) -> [UInt8] { varint(UInt64(number << 3)) + varint(value) }
    private func field(_ number: Int, bytes: [UInt8]) -> [UInt8] { varint(UInt64(number << 3 | 2)) + varint(UInt64(bytes.count)) + bytes }
    private func field(_ number: Int, string: String) -> [UInt8] { field(number, bytes: Array(string.utf8)) }
    private func attribute(_ key: String, _ value: String) -> [UInt8] {
        field(20, bytes: field(1, string: key) + field(2, string: value))
    }

    private func genMetadata(model: String, uncached: UInt64, cached: UInt64?, output: UInt64, request: String, step: Int) -> Data {
        var usage = field(2, varint: uncached) + field(3, varint: output)
        if let cached { usage += field(5, varint: cached) }
        let meta = field(4, bytes: usage) + field(19, string: model)
            + attribute("request_id", request) + attribute("last_step_index", String(step))
        return Data(field(1, bytes: meta))
    }

    private func stepMetadata(seconds: UInt64) -> Data { Data(field(1, bytes: field(1, varint: seconds))) }

    // SQLITE_TRANSIENT is a C macro Swift cannot import: SQLite copies the bound blob.
    private let transient = unsafeBitCast(-1, to: sqlite3_destructor_type.self)

    private func exec(_ database: OpaquePointer?, _ sql: String) {
        XCTAssertEqual(sqlite3_exec(database, sql, nil, nil, nil), SQLITE_OK, sql)
    }

    private func insert(_ database: OpaquePointer?, _ sql: String, idx: Int, _ data: Data) {
        var statement: OpaquePointer?
        XCTAssertEqual(sqlite3_prepare_v2(database, sql, -1, &statement, nil), SQLITE_OK)
        defer { sqlite3_finalize(statement) }
        sqlite3_bind_int64(statement, 1, Int64(idx))
        _ = data.withUnsafeBytes { sqlite3_bind_blob(statement, 2, $0.baseAddress, Int32(data.count), transient) }
        XCTAssertEqual(sqlite3_step(statement), SQLITE_DONE)
    }

    /// Creates the schema on first use and appends one call per entry, at the given step time.
    private func append(_ calls: [(idx: Int, data: Data, seconds: UInt64)]) {
        var database: OpaquePointer?
        XCTAssertEqual(sqlite3_open_v2(dbPath, &database, SQLITE_OPEN_READWRITE | SQLITE_OPEN_CREATE, nil), SQLITE_OK)
        defer { sqlite3_close_v2(database) }
        exec(database, """
            CREATE TABLE IF NOT EXISTS trajectory_meta (trajectory_id text PRIMARY KEY, cascade_id text);
            CREATE TABLE IF NOT EXISTS gen_metadata (idx integer PRIMARY KEY, data blob);
            CREATE TABLE IF NOT EXISTS steps (idx integer PRIMARY KEY, metadata blob);
            INSERT OR IGNORE INTO trajectory_meta VALUES ('t1', 'cascade-1');
            """)
        for call in calls {
            insert(database, "INSERT INTO gen_metadata (idx, data) VALUES (?1, ?2)", idx: call.idx, call.data)
            insert(database, "INSERT OR REPLACE INTO steps (idx, metadata) VALUES (?1, ?2)", idx: call.idx, stepMetadata(seconds: call.seconds))
        }
    }

    private func scanner() -> ApiCostScanner {
        let root = dir.appending(path: "conversations")
        return ApiCostScanner(ledgerDirectory: dir, sources: [.init(tool: .antigravity, kind: .antigravity, roots: [root])],
                              timeZone: utc, now: { apiCostDate("2026-11-24T12:00:00Z") })
    }

    // MARK: Tests

    func testReadsTokensModelTimestampAndKey() throws {
        append([(0, genMetadata(model: "gemini-4-argon-trial", uncached: 10_690, cached: nil, output: 465, request: "t1-0", step: 0), 1_795_000_000)])
        let result = try XCTUnwrap(AntigravityConversationReader.read(path: dbPath, afterIdx: -1, fallback: Date(timeIntervalSince1970: 0)))
        let record = try XCTUnwrap(result.records.first)
        XCTAssertEqual(record.model, "gemini-4-argon-trial")
        XCTAssertEqual(record.tokens, TokenTotals(input: 10_690, output: 465))
        XCTAssertEqual(record.key, "t1-0")
        XCTAssertEqual(record.timestamp, Date(timeIntervalSince1970: 1_795_000_000))
        XCTAssertEqual(result.lastIdx, 0)
        XCTAssertEqual(AntigravityConversationReader.identity(path: dbPath), "cascade-1")
    }

    func testReadingALeftOverWalDatabaseCreatesNoFilesAndDropsEffortSuffix() throws {
        append([(0, genMetadata(model: "claude-opus-5-5-medium", uncached: 100, cached: nil, output: 10, request: "t1-0", step: 0), 1_795_000_000)])
        var database: OpaquePointer?
        XCTAssertEqual(sqlite3_open_v2(dbPath, &database, SQLITE_OPEN_READWRITE, nil), SQLITE_OK)
        exec(database, "PRAGMA journal_mode=WAL;")
        sqlite3_close_v2(database)
        XCTAssertFalse(FileManager.default.fileExists(atPath: dbPath + "-wal"))

        let result = try XCTUnwrap(AntigravityConversationReader.read(path: dbPath, afterIdx: -1, fallback: Date(timeIntervalSince1970: 0)))
        XCTAssertEqual(result.records.first?.model, "claude-opus-5-5")
        // The IDE is not running (no -wal), so reading must not leave -wal/-shm behind.
        XCTAssertFalse(FileManager.default.fileExists(atPath: dbPath + "-wal"))
        XCTAssertFalse(FileManager.default.fileExists(atPath: dbPath + "-shm"))
    }

    func testPricesCachedPromptAtTheCacheRate() throws {
        append([(0, genMetadata(model: "gemini-3.8-flash-n", uncached: 1_000_000, cached: 1_000_000, output: 1_000_000, request: "t1-0", step: 0), 1_795_000_000)])
        let estimate = try XCTUnwrap(scanner().scan(tools: [.antigravity]).estimates[.antigravity])
        // 0.75 input + 0.075 cache read + 3.75 output per million.
        XCTAssertEqual(estimate.costUSD, Decimal(string: "4.575", locale: Locale(identifier: "en_US_POSIX")))
        XCTAssertTrue(estimate.unpricedModels.isEmpty)
    }

    func testRescanDoesNotDoubleCountAndResumesAfterNewCalls() throws {
        append([(0, genMetadata(model: "gemini-4-argon", uncached: 100, cached: nil, output: 10, request: "t1-0", step: 0), 1_795_000_000)])
        let first = try XCTUnwrap(scanner().scan(tools: [.antigravity]).estimates[.antigravity])
        XCTAssertEqual(first.pricedTokens.total, 110)

        // Unchanged database: nothing is read twice.
        let again = try XCTUnwrap(scanner().scan(tools: [.antigravity]).estimates[.antigravity])
        XCTAssertEqual(again.pricedTokens.total, 110)

        append([(1, genMetadata(model: "gemini-4-argon", uncached: 50, cached: 100, output: 5, request: "t1-1", step: 1), 1_795_000_100)])
        let resumed = try XCTUnwrap(scanner().scan(tools: [.antigravity]).estimates[.antigravity])
        XCTAssertEqual(resumed.pricedTokens.total, 110 + 155)
    }

    func testUnknownModelCountsTokensWithoutDollars() throws {
        append([(0, genMetadata(model: "gemini-9-unknown", uncached: 100, cached: nil, output: 10, request: "t1-0", step: 0), 1_795_000_000)])
        let estimate = try XCTUnwrap(scanner().scan(tools: [.antigravity]).estimates[.antigravity])
        XCTAssertEqual(estimate.unpricedModels, ["gemini-9-unknown"])
        XCTAssertEqual(estimate.unpricedTokens, 110)
    }
}
