import Foundation
import SQLite3

/// A failed SQLite call, carrying the result code (never any row data).
struct HistoryDatabaseError: Error, CustomStringConvertible {
    let code: Int32

    var description: String { "SQLite error \(code)" }

    /// The file is not a usable database: the only failures that justify deleting it.
    /// Anything else (a constraint, a full disk, a lock) leaves the history alone.
    var isCorruption: Bool {
        let primary = code & 0xFF
        return primary == SQLITE_CORRUPT || primary == SQLITE_NOTADB
    }
}

/// The few SQLite calls the usage history needs, over the system SQLite C API. Not
/// thread-safe on its own: `UsageHistoryStore` serializes every use under its lock.
final class HistoryDatabase {
    private var handle: OpaquePointer?

    init(path: String) throws {
        var opened: OpaquePointer?
        let code = sqlite3_open_v2(path, &opened, SQLITE_OPEN_READWRITE | SQLITE_OPEN_CREATE | SQLITE_OPEN_NOMUTEX, nil)
        guard code == SQLITE_OK, let opened else {
            sqlite3_close_v2(opened)
            throw HistoryDatabaseError(code: code)
        }
        handle = opened
        sqlite3_busy_timeout(opened, 250)
    }

    deinit { close() }

    func close() {
        if let handle { sqlite3_close_v2(handle) }
        handle = nil
    }

    func execute(_ sql: String) throws {
        let statement = try prepare(sql)
        defer { statement.finalize() }
        while try statement.step() {}
    }

    func prepare(_ sql: String) throws -> HistoryStatement {
        guard let handle else { throw HistoryDatabaseError(code: SQLITE_MISUSE) }
        var statement: OpaquePointer?
        let code = sqlite3_prepare_v2(handle, sql, -1, &statement, nil)
        guard code == SQLITE_OK, let statement else {
            sqlite3_finalize(statement)
            throw HistoryDatabaseError(code: code)
        }
        return HistoryStatement(statement)
    }

    /// Runs `body` inside one transaction, rolled back if it throws.
    func transaction(_ body: () throws -> Void) throws {
        try execute("BEGIN")
        do {
            try body()
            try execute("COMMIT")
        } catch {
            try? execute("ROLLBACK")
            throw error
        }
    }
}

final class HistoryStatement {
    // SQLITE_TRANSIENT is a C macro Swift cannot import: SQLite copies the bound text.
    private static let transient = unsafeBitCast(-1, to: sqlite3_destructor_type.self)

    private var statement: OpaquePointer?

    init(_ statement: OpaquePointer) {
        self.statement = statement
    }

    deinit { finalize() }

    func finalize() {
        if let statement { sqlite3_finalize(statement) }
        statement = nil
    }

    func reset() {
        sqlite3_reset(statement)
        sqlite3_clear_bindings(statement)
    }

    func bind(_ index: Int32, _ value: String?) {
        if let value { sqlite3_bind_text(statement, index, value, -1, Self.transient) }
        else { sqlite3_bind_null(statement, index) }
    }

    func bind(_ index: Int32, _ value: Int64?) {
        if let value { sqlite3_bind_int64(statement, index, value) }
        else { sqlite3_bind_null(statement, index) }
    }

    func bind(_ index: Int32, _ value: Double) {
        sqlite3_bind_double(statement, index, value)
    }

    /// Advances one row: true while a row is available, false once done.
    func step() throws -> Bool {
        let code = sqlite3_step(statement)
        switch code {
        case SQLITE_ROW: return true
        case SQLITE_DONE: return false
        default: throw HistoryDatabaseError(code: code)
        }
    }

    /// Runs a statement that returns no rows.
    func run() throws {
        while try step() {}
    }

    func string(_ column: Int32) -> String {
        sqlite3_column_text(statement, column).map { String(cString: $0) } ?? ""
    }

    func int64(_ column: Int32) -> Int64 {
        sqlite3_column_int64(statement, column)
    }

    func optionalInt64(_ column: Int32) -> Int64? {
        sqlite3_column_type(statement, column) == SQLITE_NULL ? nil : sqlite3_column_int64(statement, column)
    }

    func double(_ column: Int32) -> Double {
        sqlite3_column_double(statement, column)
    }
}
