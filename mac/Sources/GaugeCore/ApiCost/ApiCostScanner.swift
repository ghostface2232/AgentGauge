import CryptoKit
import Foundation

/// Turns the Claude Code and Codex session logs on this Mac into per-tool monthly
/// API-cost estimates, reading only what changed since the last run. Each file is read
/// from the byte where the previous scan stopped; a file whose size shrank or whose
/// identity no longer matches is forgotten and read again from the start. Work is bounded
/// per scan (bytes and wall time) so a first run over gigabytes of history finishes across
/// a few passes instead of one long one; newest files go first so the current month is
/// right before older history is complete.
///
/// Synchronous and blocking by design — `ApiCostService` runs it off the cooperative pool.
public struct ApiCostScanner: Sendable {
    public enum LogKind: Sendable { case claude, codex }

    /// Where one tool's logs live. Roots that do not exist are simply empty.
    public struct LogSource: Sendable {
        public var tool: ToolKind
        public var kind: LogKind
        public var roots: [URL]

        public init(tool: ToolKind, kind: LogKind, roots: [URL]) {
            self.tool = tool
            self.kind = kind
            self.roots = roots
        }
    }

    /// One scan's outcome. `isComplete` is false when the scan stopped at its byte or time
    /// budget with files still unread, so its estimates undercount: the caller scans again
    /// (the ledger resumes where this one stopped) and publishes only a complete result.
    public struct Result: Sendable {
        public var estimates: [ToolKind: ApiCostEstimate]
        public var isComplete: Bool
    }

    public static let ledgerFileName = "api-cost-ledger.json"
    public static let defaultMaxBytesPerScan: Int64 = 512 * 1024 * 1024
    public static let maxDurationPerScan: TimeInterval = 20
    static let readChunkBytes = 1024 * 1024
    static let fingerprintBytes = 4096

    private let ledgerURL: URL
    private let sources: [LogSource]
    private let calendar: Calendar
    private let maxBytesPerScan: Int64
    private let clock: any MonotonicClock
    private let now: @Sendable () -> Date

    public init(ledgerDirectory: URL = UsageCacheStore.defaultDirectory(),
                sources: [LogSource] = ApiCostScanner.defaultSources(),
                timeZone: TimeZone = .current,
                maxBytesPerScan: Int64 = ApiCostScanner.defaultMaxBytesPerScan,
                clock: any MonotonicClock = SystemMonotonicClock(),
                now: @escaping @Sendable () -> Date = { Date() }) {
        ledgerURL = ledgerDirectory.appending(path: Self.ledgerFileName)
        self.sources = sources
        var calendar = Calendar(identifier: .gregorian)
        calendar.timeZone = timeZone
        self.calendar = calendar
        self.maxBytesPerScan = maxBytesPerScan
        self.clock = clock
        self.now = now
    }

    /// The default sources: Claude Code's project transcripts (`$CLAUDE_CONFIG_DIR/projects`
    /// alone when that is set) and Codex's session rollouts (under `$CODEX_HOME` when set).
    public static func defaultSources(home: URL = FileManager.default.homeDirectoryForCurrentUser,
                                      environment: [String: String] = ProcessInfo.processInfo.environment) -> [LogSource] {
        let claudeRoots: [URL]
        if let configured = environment["CLAUDE_CONFIG_DIR"], !configured.isEmpty {
            claudeRoots = [URL(filePath: configured, directoryHint: .isDirectory).appending(path: "projects")]
        } else {
            claudeRoots = [home.appending(path: ".claude/projects"), home.appending(path: ".config/claude/projects")]
        }
        let codexHome = environment["CODEX_HOME"].flatMap { $0.isEmpty ? nil : URL(filePath: $0, directoryHint: .isDirectory) }
            ?? home.appending(path: ".codex")
        return [
            LogSource(tool: .claude, kind: .claude, roots: claudeRoots),
            LogSource(tool: .codex, kind: .codex,
                      roots: [codexHome.appending(path: "sessions"), codexHome.appending(path: "archived_sessions")]),
        ]
    }

    /// Scans the logs of `tools` and returns the current local month's estimate for each
    /// tool that has a source. The ledger is saved after every scan, complete or
    /// budget-limited; a cancelled scan throws `CancellationError` without saving.
    public func scan(tools: Set<ToolKind>, isCancelled: () -> Bool = { false }) throws -> Result {
        let ledger = ApiCostLedger.load(from: ledgerURL)
        let now = now()
        let oldestDay = dayKey(now.addingTimeInterval(-TimeInterval(ApiCostLedger.retentionDays) * 86_400))
        var context = ScanContext(started: clock.now(), budget: maxBytesPerScan, decoder: JSONDecoder())
        var complete = true
        let selected = sources.filter { tools.contains($0.tool) }

        sourceLoop: for source in selected {
            let files = source.roots.flatMap(Self.enumerate).sorted { $0.lastWriteMs > $1.lastWriteMs }
            let present = Set(files.map(\.path))
            // Ledger entries whose file is gone: candidates for a file that moved (Codex
            // moves a rollout into archived_sessions when a thread is archived, keeping its
            // bytes and write time).
            var vanished = ledger.files
                .filter { $0.value.tool == source.tool.rawValue && $0.value.fingerprint != nil && !present.contains($0.key) }
                .map(\.key)
                .sorted()

            for file in files {
                if isCancelled() { throw CancellationError() }
                if context.budget <= 0 || clock.now() - context.started > Self.maxDurationPerScan {
                    // Out of budget with this file (and any after it) not yet checked. An
                    // unchanged file would cost nothing, but telling them apart means reading
                    // metadata the next scan re-reads anyway, so stop and report incomplete.
                    complete = false
                    break sourceLoop
                }
                adoptIfMoved(ledger, file, &vanished, oldestDay)
                let (read, finished) = try scanFile(ledger, source, file, oldestDay, &context, isCancelled)
                context.budget -= read
                if !finished {
                    // Stopped partway through this file at the byte or time budget; the next
                    // scan resumes at the byte where this one stopped.
                    complete = false
                    break sourceLoop
                }
            }
        }

        // A write time of 0 means the file has not been read to its end yet (a new file whose
        // only line is still being written): it is in progress, not old, and dropping it
        // would re-hash and re-read it on every scan. A gone file is still pruned.
        ledger.prune(
            oldestDay: oldestDay,
            exists: { FileManager.default.fileExists(atPath: $0) },
            writtenBeforeWindow: { [self] ms in ms != 0 && dayKey(Date(timeIntervalSince1970: TimeInterval(ms) / 1000)) < oldestDay })
        ledger.save(to: ledgerURL)

        let month = calendar.dateComponents([.year, .month], from: now)
        var estimates: [ToolKind: ApiCostEstimate] = [:]
        for source in selected {
            estimates[source.tool] = Self.estimate(ledger, tool: source.tool, year: month.year ?? 0, month: month.month ?? 0, scannedAt: now)
        }
        return Result(estimates: estimates, isComplete: complete)
    }

    /// Prices one tool's ledger rows for a month; unknown models count tokens only.
    static func estimate(_ ledger: ApiCostLedger, tool: ToolKind, year: Int, month: Int, scannedAt: Date) -> ApiCostEstimate {
        var cost = Decimal(0)
        var priced = TokenTotals.zero
        var unpriced: Int64 = 0
        var unpricedModels: [String: String] = [:]
        for row in ledger.rows(tool: tool.rawValue, year: year, month: month) {
            if ApiCostPricing.isExcluded(row.model) { continue }
            if let rowCost = ApiCostPricing.cost(row.model, row.tokens, longContext: row.longContext) {
                cost += rowCost
                priced = priced + row.tokens
            } else {
                unpriced += row.tokens.total
                unpricedModels[row.model.lowercased()] = unpricedModels[row.model.lowercased()] ?? row.model
            }
        }
        let names = unpricedModels.sorted { $0.key < $1.key }.map(\.value)
        return ApiCostEstimate(tool: tool, year: year, month: month, costUSD: cost, pricedTokens: priced,
                               unpricedTokens: unpriced, unpricedModels: names, scannedAt: scannedAt)
    }

    // MARK: - Files

    struct LogFile {
        var path: String
        var length: Int64
        var lastWriteMs: Int64
    }

    private struct ScanContext {
        let started: TimeInterval
        var budget: Int64
        let decoder: JSONDecoder
    }

    // A file the ledger does not know, whose opening bytes match an entry whose file has
    // vanished, is that file moved: re-key the entry instead of counting it again.
    private func adoptIfMoved(_ ledger: ApiCostLedger, _ file: LogFile, _ vanished: inout [String], _ oldestDay: String) {
        guard !vanished.isEmpty, ledger.files[file.path] == nil else { return }
        // A file older than the window is never entered in the ledger, so it cannot be one
        // that moved out from under an entry; don't hash it against every vanished entry.
        if isBeforeWindow(file, oldestDay) { return }
        for (index, old) in vanished.enumerated() {
            guard let state = ledger.files[old], Int64(state.fingerprintLength) <= file.length else { continue }
            guard Self.fingerprint(file.path, length: state.fingerprintLength) == state.fingerprint else { continue }
            ledger.move(from: old, to: file.path)
            vanished.remove(at: index)
            return
        }
    }

    // Returns the bytes read from this file, and finished = false when the byte or time
    // budget ran out before the file's end (the caller then reports the scan incomplete). A
    // file that cannot be opened right now (another process holding it, a transient IO
    // error) is skipped for this pass and counts as finished: its already-counted rows stay
    // in the ledger, so the published total is at worst briefly stale, never lower.
    private func scanFile(_ ledger: ApiCostLedger, _ source: LogSource, _ file: LogFile, _ oldestDay: String,
                          _ context: inout ScanContext, _ isCancelled: () -> Bool) throws -> (read: Int64, finished: Bool) {
        let path = file.path
        var state = ledger.files[path]
        if let known = state {
            if known.length == file.length && known.lastWriteMs == file.lastWriteMs { return (0, true) }
            if file.length < known.parsedBytes {
                // Shrunk: the parsed prefix no longer describes this file.
                ledger.forget(path)
                state = nil
            } else {
                // Rewritten in place? Unverifiable right now (the read failed) is not the
                // same as changed: skip this pass rather than drop the file's rows.
                guard let current = Self.fingerprint(path, length: known.fingerprintLength) else { return (0, true) }
                if known.fingerprint == nil {
                    // An entry written before identities were required: adopt it now.
                    known.fingerprint = current
                } else if current != known.fingerprint {
                    ledger.forget(path)
                    state = nil
                }
            }
        }
        let entry: ApiCostLedger.FileState
        if let state {
            entry = state
        } else {
            // A file last written before the retention window cannot add a row we would
            // keep. It is skipped without an entry; if it is ever written again its write
            // time moves into the window and it is read then.
            if isBeforeWindow(file, oldestDay) { return (0, true) }
            let length = Int(min(Int64(Self.fingerprintBytes), file.length))
            // No entry without an identity: without one a rewrite or a move could not be
            // told apart from new data. Unreadable now, it is picked up on a later scan.
            guard let fingerprint = Self.fingerprint(path, length: length) else { return (0, true) }
            entry = ApiCostLedger.FileState(tool: source.tool.rawValue, fingerprint: fingerprint, fingerprintLength: length)
            ledger.files[path] = entry
        }

        var read: Int64 = 0
        var outOfBudget = false
        var reachedEnd = false
        var consumed = entry.parsedBytes
        var codex = source.kind == .codex ? CodexUsageLogParser(state: entry.codex, decoder: context.decoder) : nil
        let decoder = context.decoder
        let budget = context.budget
        let started = context.started
        defer {
            // Whatever was added to the ledger is recorded as consumed, even when the read
            // failed partway, so a retry resumes after it instead of adding it again.
            entry.parsedBytes = consumed
            entry.codex = codex?.state
        }
        do {
            let handle = try FileHandle(forReadingFrom: URL(filePath: path))
            defer { try? handle.close() }
            try handle.seek(toOffset: UInt64(consumed))
            try LogLines.forEach(in: handle, startingAt: consumed) { line, endOffset in
                // Checked per line, not per file: switching the option off must stop the
                // read of a multi-gigabyte rollout now, not when it ends.
                if isCancelled() { throw CancellationError() }
                // At least one line per file per pass, so a scan always makes progress.
                if read > 0 && (read >= budget || clock.now() - started > Self.maxDurationPerScan) {
                    outOfBudget = true
                    return false
                }
                read += endOffset - consumed
                consumed = endOffset
                let record = source.kind == .codex ? codex?.feed(line) : ClaudeUsageLogParser.parse(line, using: decoder)
                guard let record else { return true }
                let day = dayKey(record.timestamp)
                if day < oldestDay { return true }
                ledger.add(path: path, file: entry, key: record.key, day: day, model: record.model, tokens: record.tokens,
                           longContext: ApiCostPricing.isLongContext(record.model, record.tokens))
                return true
            }
            reachedEnd = !outOfBudget
        } catch let cancelled as CancellationError {
            throw cancelled
        } catch {
            Log.write("cost", "Log read failed: \(type(of: error))")
        }
        // The identity is recorded only once the whole file is consumed, so a partial read
        // (a line still being written, or a failed read) is revisited next scan.
        if reachedEnd && consumed >= file.length {
            entry.length = file.length
            entry.lastWriteMs = file.lastWriteMs
        }
        return (read, !outOfBudget)
    }

    private func isBeforeWindow(_ file: LogFile, _ oldestDay: String) -> Bool {
        dayKey(Date(timeIntervalSince1970: TimeInterval(file.lastWriteMs) / 1000)) < oldestDay
    }

    static func fingerprint(_ path: String, length: Int) -> String? {
        guard let handle = FileHandle(forReadingAtPath: path) else { return nil }
        defer { try? handle.close() }
        var buffer = Data()
        while buffer.count < length {
            guard let chunk = try? handle.read(upToCount: length - buffer.count), !chunk.isEmpty else { return nil }
            buffer.append(chunk)
        }
        return SHA256.hash(data: buffer).map { String(format: "%02X", $0) }.joined()
    }

    // The enumerator's prefetched size and modification date are what the scan uses, so a
    // file deleted afterwards (Claude Code's session cleanup) cannot throw; an unreadable
    // subfolder is skipped (the error handler continues) instead of emptying the whole list.
    static func enumerate(_ root: URL) -> [LogFile] {
        let keys: [URLResourceKey] = [.isRegularFileKey, .fileSizeKey, .contentModificationDateKey]
        guard let enumerator = FileManager.default.enumerator(at: root, includingPropertiesForKeys: keys,
                                                              errorHandler: { _, _ in true }) else { return [] }
        var files: [LogFile] = []
        for case let url as URL in enumerator where url.pathExtension == "jsonl" {
            guard let values = try? url.resourceValues(forKeys: Set(keys)), values.isRegularFile == true,
                  let size = values.fileSize, size > 0, let modified = values.contentModificationDate else { continue }
            files.append(LogFile(path: url.path(percentEncoded: false), length: Int64(size),
                                 lastWriteMs: Int64((modified.timeIntervalSince1970 * 1000).rounded(.down))))
        }
        return files
    }

    private func dayKey(_ date: Date) -> String {
        let parts = calendar.dateComponents([.year, .month, .day], from: date)
        return String(format: "%04d-%02d-%02d", parts.year ?? 0, parts.month ?? 0, parts.day ?? 0)
    }
}

/// Splits a log file into lines without decoding them as text.
enum LogLines {
    /// Calls `body` with each complete line (without its terminator) and the file offset just
    /// past the terminator, so the caller can resume exactly there; `body` returns false to
    /// stop. A trailing partial line is left unread for the next pass.
    static func forEach(in handle: FileHandle, startingAt start: Int64,
                        _ body: (Data, Int64) throws -> Bool) throws {
        var pending = Data()
        var offset = start
        while let chunk = try handle.read(upToCount: ApiCostScanner.readChunkBytes), !chunk.isEmpty {
            var lineStart = chunk.startIndex
            while let newline = nextNewline(in: chunk, from: lineStart) {
                var line: Data
                if pending.isEmpty {
                    line = chunk[lineStart..<newline]
                } else {
                    pending.append(chunk[lineStart..<newline])
                    line = pending
                    pending = Data()
                }
                if line.last == UInt8(ascii: "\r") { line = line.dropLast() }
                offset += Int64(newline - lineStart + 1)
                lineStart = newline + 1
                if try !body(line, offset) { return }
            }
            if lineStart < chunk.endIndex {
                pending.append(chunk[lineStart...])
                offset += Int64(chunk.endIndex - lineStart)
            }
        }
        // Whatever is pending never ended in a newline; it is not consumed.
    }

    private static func nextNewline(in chunk: Data, from index: Data.Index) -> Data.Index? {
        chunk.withUnsafeBytes { raw -> Data.Index? in
            let skip = index - chunk.startIndex
            guard let base = raw.baseAddress, raw.count > skip,
                  let found = memchr(base + skip, Int32(UInt8(ascii: "\n")), raw.count - skip) else { return nil }
            return chunk.startIndex + base.distance(to: UnsafeRawPointer(found))
        }
    }
}
