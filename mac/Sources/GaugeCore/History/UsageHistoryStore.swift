import Foundation

/// Append-only history of live usage readings, stored as
/// `~/Library/Application Support/AgentGauge/usage-history.db` (SQLite, WAL).
///
/// Every card value is an instantaneous reading; the sparkline, the ETA projection and the
/// automatic pace model need the readings over time. This store is purely additive
/// analytics — `UsageCacheStore` remains the single source of truth for the current state
/// and rehydration, and a missing or corrupt history database must never affect startup or
/// refreshes (a corrupt one is deleted and recreated instead).
///
/// Only values Gauge itself computed are stored; no tokens or credentials ever touch this
/// file. The caller records only genuinely live snapshots (whose `capturedAt` advanced), and
/// the store additionally drops any window whose capture time is not newer than the last
/// one it recorded for that window, so a provider re-serving its cached snapshot during a
/// cooldown never produces rows. The UNIQUE index is a further line of defence.
///
/// Thread-safe: every entry point runs under one lock. Reads are served from memory once a
/// window is hydrated, so they can be called on the main thread while a refresh records on
/// another; `warm()` hydrates everything up front, off the main thread.
public final class UsageHistoryStore: @unchecked Sendable {
    public static let fileName = "usage-history.db"

    static let retention: TimeInterval = 90 * 86_400
    static let pruneInterval: TimeInterval = 86_400

    /// Reads only ever need a short trailing span (the sparkline's six hours, the ETA's
    /// ninety minutes), so that much is mirrored in memory per window.
    public static let memoryTailSpan: TimeInterval = 6 * 3600

    // The automatic pace model's weekday profile is folded from this much history at
    // hydration and then kept current from each recorded reading, so — like the tail — it
    // is served from memory. Four weeks sees every weekday four times; the profile's own
    // two-week sufficiency gate decides when it may shape a curve.
    static let profileSpan: TimeInterval = 28 * 86_400

    // A utilization increase is credited to a weekday only when the previous reading is
    // this recent. Across a longer gap (a laptop asleep over a weekend) the consumption
    // belongs to days that were never observed, and crediting it to the day the machine
    // woke would teach the profile a shape the user never had.
    static let maximumProfileGap: TimeInterval = 12 * 3600

    private struct Key: Hashable {
        let tool: String
        let window: String
    }

    private let url: URL
    private let now: @Sendable () -> Date
    private let calendar: Calendar
    private let lock = NSLock()

    private var database: HistoryDatabase?
    private var tail: [Key: [UsageSample]] = [:]
    private var profiles: [Key: WeekdayAccumulator] = [:]
    private var hydratedKeys: Set<Key> = []
    private var allHydrated = false
    private var lastPrunedAt: Date?
    private var isClosed = false

    /// - Parameters:
    ///   - directory: Where the database lives; Application Support/AgentGauge by default.
    ///   - timeZone: Zone whose calendar days the weekday profile counts; injectable so
    ///     tests do not depend on where they run.
    ///   - now: Wall-clock source for the tail cutoff, the profile window and pruning.
    public init(
        directory: URL = UsageCacheStore.defaultDirectory(),
        timeZone: TimeZone = .current,
        now: @escaping @Sendable () -> Date = { Date() }
    ) {
        url = directory.appending(path: Self.fileName)
        var calendar = Calendar(identifier: .gregorian)
        calendar.timeZone = timeZone
        self.calendar = calendar
        self.now = now
    }

    deinit {
        database?.close()
    }

    /// Records every window of a live snapshot whose capture time is newer than the last
    /// reading recorded for that window. An empty snapshot (signed out, no data) records
    /// nothing.
    public func record(_ snapshot: UsageSnapshot) {
        guard !snapshot.windows.isEmpty else { return }
        lock.lock()
        defer { lock.unlock() }
        guard !isClosed else { return }

        // Stored at millisecond precision; the memory copy is rounded the same way so a
        // reading compares equal before and after a relaunch.
        let captured = Self.roundedToMillisecond(snapshot.capturedAt)
        var fresh: [(key: Key, window: UsageWindow, sample: UsageSample)] = []
        for window in snapshot.windows where window.usedRatio.isFinite {
            let key = Key(tool: snapshot.tool.rawValue, window: window.id)
            guard !fresh.contains(where: { $0.key == key }) else { continue }
            hydrate(key)
            if let last = profiles[key]?.last?.capturedAt ?? tail[key]?.last?.capturedAt, captured <= last {
                continue
            }
            let sample = UsageSample(
                capturedAt: captured,
                usedRatio: window.usedRatio,
                resetTime: window.resetTime.map(Self.roundedToMillisecond))
            fresh.append((key, window, sample))
        }
        guard !fresh.isEmpty else { return }

        withDatabase { database in
            try database.transaction {
                let insert = try database.prepare(
                    """
                    INSERT OR IGNORE INTO samples
                        (tool, window_key, group_label, captured_at, used_ratio,
                         reset_time, used_tokens, limit_tokens)
                    VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8)
                    """)
                defer { insert.finalize() }
                for entry in fresh {
                    insert.reset()
                    insert.bind(1, entry.key.tool)
                    insert.bind(2, entry.key.window)
                    insert.bind(3, entry.window.groupLabel)
                    insert.bind(4, Self.milliseconds(entry.sample.capturedAt))
                    insert.bind(5, entry.sample.usedRatio)
                    insert.bind(6, entry.sample.resetTime.map(Self.milliseconds))
                    insert.bind(7, entry.window.usedCount)
                    insert.bind(8, entry.window.limitCount)
                    try insert.run()
                }
            }
        }

        // The memory copy is kept even when the write failed, so the open popover still
        // shows the trend; only the next launch loses what never reached the disk.
        let cutoff = now().addingTimeInterval(-Self.memoryTailSpan)
        for entry in fresh {
            accumulator(entry.key).add(entry.sample, calendar: calendar)
            var list = tail[entry.key] ?? []
            list.append(entry.sample)
            list.removeAll { $0.capturedAt < cutoff }
            tail[entry.key] = list
        }
        pruneIfDue()
    }

    /// Recorded samples for one window over the trailing `lookback` (at most the six-hour
    /// memory tail), oldest first. Empty when nothing was recorded. Served from memory once
    /// the window is hydrated.
    public func recent(tool: ToolKind, windowID: String, lookback: TimeInterval = memoryTailSpan) -> [UsageSample] {
        lock.lock()
        defer { lock.unlock() }
        guard !isClosed else { return [] }
        let key = Key(tool: tool.rawValue, window: windowID)
        hydrate(key)
        let cutoff = now().addingTimeInterval(-lookback)
        return (tail[key] ?? []).filter { $0.capturedAt >= cutoff }
    }

    /// How one window's consumption has fallen across the local weekdays over the recent
    /// weeks, for the automatic pace model. Nil when nothing was recorded for the window.
    public func weekdayProfile(tool: ToolKind, windowID: String) -> UsageWeekdayProfile? {
        lock.lock()
        defer { lock.unlock() }
        guard !isClosed else { return nil }
        let key = Key(tool: tool.rawValue, window: windowID)
        hydrate(key)
        return profiles[key]?.profile()
    }

    /// Hydrates every window's memory tail and folds every weekday profile now. Meant to be
    /// called once off the main thread at startup, so the 28-day fold is not paid by
    /// whichever card reads history first on the main thread. Idempotent, and harmless if
    /// never called.
    public func warm() {
        lock.lock()
        defer { lock.unlock() }
        guard !isClosed, !allHydrated else { return }
        allHydrated = true
        guard database != nil || FileManager.default.fileExists(atPath: url.path) else { return }
        let current = now()
        let since = Self.milliseconds(current.addingTimeInterval(-Self.profileSpan))
        let tailSince = current.addingTimeInterval(-Self.memoryTailSpan)
        var loaded: Set<Key> = []
        withDatabase { database in
            // Ordered per window so consecutive rows are consecutive readings; the fold
            // credits each increase to the local weekday it was observed on.
            let query = try database.prepare(
                """
                SELECT tool, window_key, captured_at, used_ratio, reset_time FROM samples
                WHERE captured_at >= ?1 ORDER BY tool, window_key, captured_at
                """)
            defer { query.finalize() }
            query.bind(1, since)
            while try query.step() {
                let key = Key(tool: query.string(0), window: query.string(1))
                // A window a reader already hydrated on its own is folded already.
                guard !hydratedKeys.contains(key) else { continue }
                loaded.insert(key)
                fold(Self.sample(query, from: 2), into: key, tailSince: tailSince)
            }
        }
        hydratedKeys.formUnion(loaded)
    }

    /// Closes the database. Later calls record nothing and read empty.
    public func close() {
        lock.lock()
        defer { lock.unlock() }
        isClosed = true
        closeDatabase()
    }

    // MARK: - Hydration

    /// Loads one window's last 28 days from disk once, so later reads are memory-only: the
    /// six-hour tail and the weekday profile come from the same rows. Marked hydrated before
    /// the read, so a failing database is not retried on every call.
    private func hydrate(_ key: Key) {
        guard !allHydrated, !hydratedKeys.contains(key) else { return }
        hydratedKeys.insert(key)
        // Nothing recorded yet: reading must not create the database, so a fresh install
        // that never gets a live reading leaves no file behind.
        guard database != nil || FileManager.default.fileExists(atPath: url.path) else { return }
        let current = now()
        let since = Self.milliseconds(current.addingTimeInterval(-Self.profileSpan))
        let tailSince = current.addingTimeInterval(-Self.memoryTailSpan)
        withDatabase { database in
            let query = try database.prepare(
                """
                SELECT captured_at, used_ratio, reset_time FROM samples
                WHERE tool = ?1 AND window_key = ?2 AND captured_at >= ?3 ORDER BY captured_at
                """)
            defer { query.finalize() }
            query.bind(1, key.tool)
            query.bind(2, key.window)
            query.bind(3, since)
            while try query.step() {
                fold(Self.sample(query, from: 0), into: key, tailSince: tailSince)
            }
        }
    }

    private func fold(_ sample: UsageSample, into key: Key, tailSince: Date) {
        accumulator(key).add(sample, calendar: calendar)
        if sample.capturedAt >= tailSince {
            tail[key, default: []].append(sample)
        }
    }

    private func accumulator(_ key: Key) -> WeekdayAccumulator {
        if let existing = profiles[key] { return existing }
        let created = WeekdayAccumulator()
        profiles[key] = created
        return created
    }

    private static func sample(_ row: HistoryStatement, from column: Int32) -> UsageSample {
        UsageSample(
            capturedAt: date(milliseconds: row.int64(column)),
            usedRatio: row.double(column + 1),
            resetTime: row.optionalInt64(column + 2).map(date(milliseconds:)))
    }

    // MARK: - Pruning

    private func pruneIfDue() {
        let current = now()
        if let lastPrunedAt, current.timeIntervalSince(lastPrunedAt) < Self.pruneInterval { return }
        lastPrunedAt = current
        let cutoff = Self.milliseconds(current.addingTimeInterval(-Self.retention))
        withDatabase { database in
            let delete = try database.prepare("DELETE FROM samples WHERE captured_at < ?1")
            defer { delete.finalize() }
            delete.bind(1, cutoff)
            try delete.run()
        }
    }

    // MARK: - Database

    /// Runs one operation against the open database. A corrupt database is deleted and
    /// recreated once (history is expendable analytics); any further failure is logged and
    /// swallowed so recording can never break a refresh cycle.
    private func withDatabase(_ body: (HistoryDatabase) throws -> Void) {
        for attempt in 0..<2 {
            do {
                try body(try openDatabase())
                return
            } catch {
                // Neutral wording on purpose: this runs for recording, hydrating and pruning
                // alike, so naming one of them would misdirect triage.
                Log.write("history", "Usage history access failed: \(error)")
                closeDatabase()
                if attempt == 0, let failure = error as? HistoryDatabaseError, failure.isCorruption {
                    deleteDatabaseFiles()
                    continue
                }
                return
            }
        }
    }

    private func openDatabase() throws -> HistoryDatabase {
        if let database { return database }
        try FileManager.default.createDirectory(
            at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
        let opened = try HistoryDatabase(path: url.path)
        do {
            try opened.execute("PRAGMA journal_mode=WAL")
            try opened.execute(
                """
                CREATE TABLE IF NOT EXISTS samples (
                    tool         TEXT    NOT NULL,
                    window_key   TEXT    NOT NULL,
                    group_label  TEXT    NULL,
                    captured_at  INTEGER NOT NULL,
                    used_ratio   REAL    NOT NULL,
                    reset_time   INTEGER NULL,
                    used_tokens  INTEGER NULL,
                    limit_tokens INTEGER NULL
                )
                """)
            try opened.execute(
                "CREATE UNIQUE INDEX IF NOT EXISTS ix_samples_identity ON samples (tool, window_key, captured_at)")
        } catch {
            // Ownership passes to `database` only once initialization succeeded; until then
            // this handle must be closed before corrupt-file recovery deletes the file.
            opened.close()
            throw error
        }
        database = opened
        return opened
    }

    private func closeDatabase() {
        database?.close()
        database = nil
    }

    private func deleteDatabaseFiles() {
        for path in [url.path, url.path + "-wal", url.path + "-shm"] where FileManager.default.fileExists(atPath: path) {
            do {
                try FileManager.default.removeItem(atPath: path)
            } catch {
                Log.write("history", "Usage history reset failed: \(type(of: error))")
            }
        }
    }

    // MARK: - Time

    static func milliseconds(_ date: Date) -> Int64 {
        Int64((date.timeIntervalSince1970 * 1000).rounded())
    }

    static func date(milliseconds: Int64) -> Date {
        Date(timeIntervalSince1970: Double(milliseconds) / 1000)
    }

    static func roundedToMillisecond(_ date: Date) -> Date {
        Self.date(milliseconds: milliseconds(date))
    }
}

/// One window's running weekday profile: the utilization increases per local weekday and
/// the distinct local dates seen. A decrease is not consumption (a provider recomputing its
/// headline percent) — except across a reset, where the new reading is what was used since
/// it. An increase across more than `UsageHistoryStore.maximumProfileGap` is dropped rather
/// than credited to the day the readings resumed.
///
/// The accumulator keeps its own previous reading, so the hydration fold and the live path
/// compare against the same one: after a restart the first live reading is paired with the
/// database's last row, not with whatever the six-hour tail happened to hold.
final class WeekdayAccumulator {
    // A reset moves the window's reset time forward by (roughly) its length; providers also
    // jitter it by seconds between polls, which must not read as a reset.
    static let minimumResetAdvance: TimeInterval = 3600

    private var sums = [Double](repeating: 0, count: 7)
    // Distinct local dates seen (y*10000 + m*100 + d) and the weekday index each fell on.
    private var days: [Int: Int] = [:]
    private(set) var last: UsageSample?

    func add(_ current: UsageSample, calendar: Calendar) {
        // Readings arrive in capture order; a re-recorded or older one changes nothing.
        if let last, current.capturedAt <= last.capturedAt { return }
        let previous = last
        last = current

        let local = calendar.dateComponents([.year, .month, .day, .weekday], from: current.capturedAt)
        let weekday = (local.weekday ?? 1) - 1
        days[(local.year ?? 0) * 10_000 + (local.month ?? 0) * 100 + (local.day ?? 0)] = weekday
        guard let previous,
              current.capturedAt.timeIntervalSince(previous.capturedAt) <= UsageHistoryStore.maximumProfileGap
        else { return }
        var delta = current.usedRatio - previous.usedRatio
        if delta < 0, let reset = current.resetTime, let lastReset = previous.resetTime,
           reset.timeIntervalSince(lastReset) >= Self.minimumResetAdvance {
            // The window rolled over between the readings: everything now shown was used
            // after the reset (a lower bound — use before it is not observable).
            delta = current.usedRatio
        }
        guard delta > 0, delta.isFinite else { return }
        sums[weekday] += delta
    }

    // The mean per observed occurrence of each weekday, not the raw sum: past the two-week
    // gate the weekdays are rarely seen equally often (day 17 has three Mondays but two
    // Thursdays), and a sum would weight the more-seen days up — an even spender would read
    // as off pace. A weekday never observed stays 0.
    func profile() -> UsageWeekdayProfile {
        var occurrences = [Int](repeating: 0, count: 7)
        for weekday in days.values { occurrences[weekday] += 1 }
        let weights = (0..<7).map { occurrences[$0] > 0 ? sums[$0] / Double(occurrences[$0]) : 0 }
        return UsageWeekdayProfile(weights: weights, daysObserved: days.count)
    }
}
