import Foundation

/// What the API-cost scanner remembers between runs, persisted as
/// `~/Library/Application Support/AgentGauge/api-cost-ledger.json`: per scanned file, how
/// far it was read, a fingerprint of its opening bytes, and the per-day, per-model token
/// totals that file contributed; plus, per response key, what was counted for it and where.
/// Holding totals per file — not just per day — is what lets a replaced or truncated file be
/// forgotten and rescanned without double counting the days it touched. Nothing here is
/// conversation content: only paths, byte offsets, a hash, model names, counts and
/// identifiers.
final class ApiCostLedger: Codable {
    // Tracks the Windows ledger's version so both platforms re-file at the same releases.
    // 4: the long-context tier is filed per response at scan time, and a model without a
    // row is filed in the standard tier, so responses scanned before a model with a tier
    // was priced would stay there. Bump whenever a newly priced model carries a
    // long-context tier; a mismatched ledger is rebuilt from the logs.
    static let currentVersion = 4

    /// Days kept: the current month, however long, plus the whole previous one.
    static let retentionDays = 62

    var version = ApiCostLedger.currentVersion

    /// Scan state keyed by full path.
    var files: [String: FileState] = [:]

    /// Responses already counted, by key, with what was counted and where.
    var keys: [String: KeyEntry] = [:]

    init() {}

    final class FileState: Codable {
        var tool = ""
        var length: Int64 = 0
        var lastWriteMs: Int64 = 0
        var parsedBytes: Int64 = 0
        /// Hash of the first `fingerprintLength` bytes, taken when first read.
        var fingerprint: String?
        var fingerprintLength = 0
        var codex: CodexUsageLogParser.State?
        var days: [DayRow] = []

        init(tool: String, fingerprint: String, fingerprintLength: Int) {
            self.tool = tool
            self.fingerprint = fingerprint
            self.fingerprintLength = fingerprintLength
        }

        enum CodingKeys: String, CodingKey {
            case tool, length, lastWriteMs, parsedBytes, fingerprint, fingerprintLength, codex, days
        }

        // Valid JSON can still carry nulls where values belong; normalize rather than let a
        // hand-edited or half-written file fail every scan from here on.
        init(from decoder: Decoder) throws {
            let c = try decoder.container(keyedBy: CodingKeys.self)
            tool = (try? c.decodeIfPresent(String.self, forKey: .tool)) ?? ""
            length = (try? c.decodeIfPresent(Int64.self, forKey: .length)) ?? 0
            lastWriteMs = (try? c.decodeIfPresent(Int64.self, forKey: .lastWriteMs)) ?? 0
            parsedBytes = (try? c.decodeIfPresent(Int64.self, forKey: .parsedBytes)) ?? 0
            fingerprint = try? c.decodeIfPresent(String.self, forKey: .fingerprint)
            fingerprintLength = (try? c.decodeIfPresent(Int.self, forKey: .fingerprintLength)) ?? 0
            codex = try? c.decodeIfPresent(CodexUsageLogParser.State.self, forKey: .codex)
            days = ((try? c.decodeIfPresent([DayRow?].self, forKey: .days)) ?? []).compactMap { $0 }
        }
    }

    /// One counted response: the file, day, model and tier row its tokens went into, and the
    /// tokens themselves, so a better write of the same response can replace them.
    final class KeyEntry: Codable {
        var path: String
        var day: String
        var model: String
        var longContext: Bool
        var tokens: TokenTotals

        init(path: String, day: String, model: String, longContext: Bool, tokens: TokenTotals) {
            self.path = path
            self.day = day
            self.model = model
            self.longContext = longContext
            self.tokens = tokens
        }
    }

    final class DayRow: Codable {
        /// Local calendar day as yyyy-MM-dd.
        var day: String
        var model: String
        /// Whether these responses were each above their model's long-context threshold.
        /// Decided per response at scan time — the tier cannot be recovered from a sum.
        var longContext: Bool
        var tokens = TokenTotals.zero
        var requests = 0

        init(day: String, model: String, longContext: Bool) {
            self.day = day
            self.model = model
            self.longContext = longContext
        }

        func add(_ added: TokenTotals, requests: Int) {
            tokens = tokens + added
            self.requests += requests
        }
    }

    enum CodingKeys: String, CodingKey { case version, files, keys }

    init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        version = try c.decode(Int.self, forKey: .version)
        files = ((try c.decodeIfPresent([String: FileState?].self, forKey: .files)) ?? [:]).compactMapValues { $0 }
        keys = ((try c.decodeIfPresent([String: KeyEntry?].self, forKey: .keys)) ?? [:]).compactMapValues { $0 }
    }

    /// Counts one response. Unkeyed responses (older Codex token counts) are simply added.
    /// A keyed response seen before is counted once: if this write is more complete than the
    /// one counted — Claude Code writes a streamed message several times and only the last
    /// carries the real output count — the earlier tokens are replaced in their original
    /// row; otherwise the write is ignored.
    func add(path: String, file: FileState, key: String?, day: String, model: String, tokens: TokenTotals, longContext: Bool) {
        guard let key else {
            Self.row(file, day, model, longContext).add(tokens, requests: 1)
            return
        }
        if let counted = keys[key] {
            guard Self.isMoreComplete(tokens, than: counted.tokens) else { return }
            // The replacement stays in the row the response was first counted in: the day
            // and model do not change between writes of one message, and keeping the owner
            // means forgetting that file later removes exactly what it contributed.
            if let owner = files[counted.path] {
                Self.row(owner, counted.day, counted.model, counted.longContext).add(tokens - counted.tokens, requests: 0)
            }
            counted.tokens = tokens
            return
        }
        Self.row(file, day, model, longContext).add(tokens, requests: 1)
        keys[key] = KeyEntry(path: path, day: day, model: model, longContext: longContext, tokens: tokens)
    }

    // Output grows as a message streams; everything else is fixed once the request is sent.
    private static func isMoreComplete(_ candidate: TokenTotals, than counted: TokenTotals) -> Bool {
        candidate.output > counted.output || (candidate.output == counted.output && candidate.total > counted.total)
    }

    private static func row(_ file: FileState, _ day: String, _ model: String, _ longContext: Bool) -> DayRow {
        if let row = file.days.first(where: {
            $0.day == day && $0.longContext == longContext && $0.model.caseInsensitiveCompare(model) == .orderedSame
        }) {
            return row
        }
        let row = DayRow(day: day, model: model, longContext: longContext)
        file.days.append(row)
        return row
    }

    /// Forgets files' contributions entirely — their rows and the keys they claimed.
    func forget(_ paths: Set<String>) {
        var removed = false
        for path in paths where files.removeValue(forKey: path) != nil { removed = true }
        guard removed else { return }
        keys = keys.filter { !paths.contains($0.value.path) }
    }

    func forget(_ path: String) { forget([path]) }

    /// Re-keys a file that moved (Codex archiving a session) without rescanning it.
    func move(from old: String, to new: String) {
        guard let state = files.removeValue(forKey: old) else { return }
        files[new] = state
        for entry in keys.values where entry.path == old {
            entry.path = new
        }
    }

    /// Drops everything older than `oldestDay` (yyyy-MM-dd, inclusive keep): day rows, the
    /// keys counted on those days (a copy of such a response would be dated before the window
    /// too, and is skipped before its key is looked at), and the entries of files that no
    /// longer contribute a row and either no longer exist or were last written before the
    /// window — a file like that is not reopened unless it changes.
    func prune(oldestDay: String, exists: (String) -> Bool, writtenBeforeWindow: (Int64) -> Bool) {
        for file in files.values {
            file.days.removeAll { $0.day < oldestDay }
        }
        keys = keys.filter { $0.value.day >= oldestDay }
        let gone = files.filter { $0.value.days.isEmpty && (!exists($0.key) || writtenBeforeWindow($0.value.lastWriteMs)) }
        forget(Set(gone.keys))
    }

    /// Every row of one tool inside a calendar month, across all files.
    func rows(tool: String, year: Int, month: Int) -> [DayRow] {
        let prefix = String(format: "%04d-%02d-", year, month)
        return files.values.filter { $0.tool == tool }.flatMap(\.days).filter { $0.day.hasPrefix(prefix) }
    }

    static func load(from url: URL) -> ApiCostLedger {
        guard let data = try? Data(contentsOf: url) else { return ApiCostLedger() }
        do {
            let ledger = try JSONDecoder().decode(ApiCostLedger.self, from: data)
            return ledger.version == currentVersion ? ledger : ApiCostLedger()
        } catch {
            // The ledger is a cache of what the logs already say; any broken one is rebuilt.
            Log.write("cost", "Ledger unreadable, rebuilding: \(type(of: error))")
            return ApiCostLedger()
        }
    }

    func save(to url: URL) {
        do {
            try FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
            try JSONEncoder().encode(self).write(to: url, options: .atomic)
        } catch {
            Log.write("cost", "Ledger save failed: \(type(of: error))")
        }
    }
}
