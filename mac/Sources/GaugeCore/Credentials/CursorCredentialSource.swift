import Foundation
import SQLite3

/// Cursor has no OAuth token file: its session JWT lives in the VS Code-style global state
/// database, `~/Library/Application Support/Cursor/User/globalStorage/state.vscdb`
/// (table `ItemTable`, key `cursorAuth/accessToken`). The database is opened read-only, so
/// a running Cursor is never disturbed and nothing is ever written.
public struct CursorCredentialSource: CredentialSource {
    static let accessTokenKey = "cursorAuth/accessToken"
    static let attempts = 3

    let databaseURL: URL
    let now: @Sendable () -> Date

    public init(
        homeDirectory: URL = FileManager.default.homeDirectoryForCurrentUser,
        now: @escaping @Sendable () -> Date = { Date() }
    ) {
        self.init(
            databaseURL: homeDirectory.appending(path: "Library/Application Support/Cursor/User/globalStorage/state.vscdb"),
            now: now)
    }

    init(databaseURL: URL, now: @escaping @Sendable () -> Date = { Date() }) {
        self.databaseURL = databaseURL
        self.now = now
    }

    public func read(interactive: Bool) async -> CredentialRead {
        guard FileManager.default.fileExists(atPath: databaseURL.path) else { return .missing }
        // Cursor writes this database while it runs; a read that lands on its lock is retried
        // briefly before being judged unreadable, like the CLI credential files.
        for attempt in 1...Self.attempts {
            do {
                let token = try SQLiteItemTable.value(forKey: Self.accessTokenKey, in: databaseURL)
                return CredentialParsing.cursor(accessToken: token, now: now())
            } catch {
                if attempt < Self.attempts {
                    try? await Task.sleep(for: .milliseconds(60))
                    continue
                }
                // Never log the database contents or the token; the SQLite code is enough.
                Log.write("auth", "Cursor state.vscdb read failed: \(error)")
            }
        }
        return .invalid(.unreadable)
    }
}

struct SQLiteReadError: Error, CustomStringConvertible {
    let code: Int32
    var description: String { "SQLite error \(code)" }
}

/// One-key reads from a VS Code-style `ItemTable(key, value)` database through the system
/// SQLite, opened read-only.
enum SQLiteItemTable {
    // SQLITE_TRANSIENT is a C macro Swift cannot import: SQLite copies the bound text.
    private static let transient = unsafeBitCast(-1, to: sqlite3_destructor_type.self)

    static func value(forKey key: String, in databaseURL: URL) throws -> String? {
        var database: OpaquePointer?
        let opened = sqlite3_open_v2(databaseURL.path, &database, SQLITE_OPEN_READONLY | SQLITE_OPEN_NOMUTEX, nil)
        defer { sqlite3_close_v2(database) }
        guard opened == SQLITE_OK, let database else { throw SQLiteReadError(code: opened) }
        sqlite3_busy_timeout(database, 250)

        var statement: OpaquePointer?
        let prepared = sqlite3_prepare_v2(database, "SELECT value FROM ItemTable WHERE key = ?1 LIMIT 1", -1, &statement, nil)
        defer { sqlite3_finalize(statement) }
        guard prepared == SQLITE_OK, let statement else { throw SQLiteReadError(code: prepared) }
        sqlite3_bind_text(statement, 1, key, -1, transient)

        switch sqlite3_step(statement) {
        case SQLITE_ROW:
            // VS Code stores values as TEXT, but older writers used BLOB; accept either.
            switch sqlite3_column_type(statement, 0) {
            case SQLITE_TEXT:
                return sqlite3_column_text(statement, 0).map { String(cString: $0) }
            case SQLITE_BLOB:
                guard let bytes = sqlite3_column_blob(statement, 0) else { return nil }
                let data = Data(bytes: bytes, count: Int(sqlite3_column_bytes(statement, 0)))
                return String(data: data, encoding: .utf8)
            default:
                return nil
            }
        case SQLITE_DONE:
            return nil
        case let code:
            throw SQLiteReadError(code: code)
        }
    }
}
