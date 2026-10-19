import Foundation
import SQLite3
import XCTest
@testable import GaugeCore

private final class TestClock: @unchecked Sendable {
    private let lock = NSLock()
    private var current: Date

    init(_ start: Date) { current = start }

    var now: Date {
        get { lock.withLock { current } }
        set { lock.withLock { current = newValue } }
    }
}

/// The append-only usage history: recording, duplicate suppression, lookback reads,
/// retention pruning, corrupt-database self-healing, and the weekday profile.
final class UsageHistoryStoreTests: XCTestCase {
    private var directory: URL!
    private var clock: TestClock!
    private let window = UsageWindowType.fiveHour.rawValue
    private let utc = TimeZone(identifier: "UTC")!
    // 2026-08-03 12:00 UTC, a Monday.
    private let start = Date(timeIntervalSince1970: 1_785_758_400)

    override func setUp() {
        directory = FileManager.default.temporaryDirectory
            .appending(path: "GaugeHistoryTest_\(UUID().uuidString)", directoryHint: .isDirectory)
        clock = TestClock(start)
    }

    override func tearDown() {
        try? FileManager.default.removeItem(at: directory)
    }

    private func makeStore(timeZone: TimeZone? = nil) -> UsageHistoryStore {
        let clock = self.clock!
        return UsageHistoryStore(directory: directory, timeZone: timeZone ?? utc, now: { clock.now })
    }

    private func snapshot(tool: ToolKind = .codex, _ ratio: Double, at captured: Date, reset: Date? = nil) -> UsageSnapshot {
        UsageSnapshot(tool: tool, plan: nil, windows: [
            UsageWindow(type: .fiveHour, usedRatio: ratio, resetTime: reset ?? captured.addingTimeInterval(2 * 3600)),
        ], capturedAt: captured)
    }

    private func recordAt(_ store: UsageHistoryStore, _ captured: Date, _ ratio: Double, reset: Date? = nil) {
        clock.now = captured
        store.record(snapshot(ratio, at: captured, reset: reset))
    }

    private var databasePath: String { directory.appending(path: UsageHistoryStore.fileName).path }

    private func countRows() -> Int {
        var database: OpaquePointer?
        defer { sqlite3_close_v2(database) }
        guard sqlite3_open_v2(databasePath, &database, SQLITE_OPEN_READONLY, nil) == SQLITE_OK else { return -1 }
        var statement: OpaquePointer?
        defer { sqlite3_finalize(statement) }
        guard sqlite3_prepare_v2(database, "SELECT COUNT(*) FROM samples", -1, &statement, nil) == SQLITE_OK,
              sqlite3_step(statement) == SQLITE_ROW else { return -1 }
        return Int(sqlite3_column_int64(statement, 0))
    }

    private func execute(_ sql: String) {
        var database: OpaquePointer?
        defer { sqlite3_close_v2(database) }
        XCTAssertEqual(sqlite3_open_v2(databasePath, &database, SQLITE_OPEN_READWRITE, nil), SQLITE_OK)
        XCTAssertEqual(sqlite3_exec(database, sql, nil, nil, nil), SQLITE_OK)
    }

    private func assertWeights(
        _ profile: UsageWeekdayProfile?, sunday: Double = 0, monday: Double = 0, tuesday: Double = 0,
        wednesday: Double = 0, thursday: Double = 0, friday: Double = 0, saturday: Double = 0,
        file: StaticString = #filePath, line: UInt = #line
    ) {
        guard let profile else { return XCTFail("no profile", file: file, line: line) }
        let expected = [sunday, monday, tuesday, wednesday, thursday, friday, saturday]
        XCTAssertEqual(profile.weights.count, 7, file: file, line: line)
        for (index, weight) in profile.weights.enumerated() {
            XCTAssertEqual(weight, expected[index], accuracy: 1e-6, "weekday \(index)", file: file, line: line)
        }
    }

    // MARK: - Recording and reads

    func testRecordThenRecentReturnsSamplesOldestFirst() {
        let store = makeStore()
        store.record(snapshot(0.10, at: start.addingTimeInterval(-600)))
        store.record(snapshot(0.20, at: start.addingTimeInterval(-300)))
        store.record(snapshot(0.30, at: start))

        let samples = store.recent(tool: .codex, windowID: window, lookback: 3600)
        XCTAssertEqual(samples.map(\.usedRatio), [0.10, 0.20, 0.30])
        XCTAssertLessThan(samples[0].capturedAt, samples[2].capturedAt)
    }

    func testDuplicateOrOlderCaptureTimeIsRecordedOnce() {
        let store = makeStore()
        store.record(snapshot(0.10, at: start))
        store.record(snapshot(0.10, at: start))
        // A re-served cache carrying an older capture time is not a new reading either.
        store.record(snapshot(0.05, at: start.addingTimeInterval(-60)))

        XCTAssertEqual(store.recent(tool: .codex, windowID: window, lookback: 3600).count, 1)
        XCTAssertEqual(countRows(), 1)
    }

    func testEmptySnapshotRecordsNothing() {
        let store = makeStore()
        store.record(UsageSnapshot(tool: .codex, plan: "Plus", windows: [], capturedAt: start))
        XCTAssertTrue(store.recent(tool: .codex, windowID: window).isEmpty)
        XCTAssertFalse(FileManager.default.fileExists(atPath: databasePath))
    }

    func testRecentFiltersByLookbackAndKey() {
        let store = makeStore()
        store.record(snapshot(0.10, at: start.addingTimeInterval(-3 * 3600)))
        store.record(snapshot(0.20, at: start))
        store.record(snapshot(tool: .claude, 0.90, at: start))

        let samples = store.recent(tool: .codex, windowID: window, lookback: 3600)
        XCTAssertEqual(samples.map(\.usedRatio), [0.20])
        XCTAssertTrue(store.recent(tool: .codex, windowID: "somethingElse", lookback: 3600).isEmpty)
        XCTAssertEqual(store.recent(tool: .claude, windowID: window).map(\.usedRatio), [0.90])
    }

    func testRecentSamplesSurviveReopen() {
        do {
            let store = makeStore()
            store.record(snapshot(0.40, at: start.addingTimeInterval(-120)))
            store.close()
        }
        let reopened = makeStore()
        XCTAssertEqual(reopened.recent(tool: .codex, windowID: window, lookback: 3600).map(\.usedRatio), [0.40])
    }

    func testWarmHydratesEveryWindowWithoutDoubleCounting() {
        do {
            let store = makeStore()
            recordAt(store, start, 0.10)
            recordAt(store, start.addingTimeInterval(3600), 0.30)
            store.record(snapshot(tool: .claude, 0.50, at: start.addingTimeInterval(3600)))
            store.close()
        }
        let reopened = makeStore()
        // One window hydrated lazily before warm(): warm must not fold it a second time.
        XCTAssertEqual(reopened.recent(tool: .codex, windowID: window).count, 2)
        reopened.warm()
        reopened.warm()
        XCTAssertEqual(reopened.recent(tool: .codex, windowID: window).count, 2)
        XCTAssertEqual(reopened.recent(tool: .claude, windowID: window).map(\.usedRatio), [0.50])
        assertWeights(reopened.weekdayProfile(tool: .codex, windowID: window), monday: 0.20)
    }

    func testSamplesCarryTheReportedResetTime() {
        // Cycle-boundary detection reads this off the samples, so it has to survive both the
        // memory tail and the hydration a later session starts from.
        let captured = start.addingTimeInterval(-120)
        do {
            let store = makeStore()
            store.record(snapshot(0.40, at: captured))
            XCTAssertEqual(store.recent(tool: .codex, windowID: window).first?.resetTime, captured.addingTimeInterval(7200))
            store.close()
        }
        let reopened = makeStore()
        XCTAssertEqual(reopened.recent(tool: .codex, windowID: window).first?.resetTime, captured.addingTimeInterval(7200))
    }

    func testSamplesOlderThanRetentionArePrunedOnALaterRecord() {
        let store = makeStore()
        store.record(snapshot(0.50, at: start))
        // 100 days later the next record runs the daily prune, which drops the old row.
        clock.now = start.addingTimeInterval(100 * 86_400)
        store.record(snapshot(0.10, at: clock.now))
        XCTAssertEqual(countRows(), 1)
    }

    func testCorruptDatabaseIsRecreatedAndRecordingContinues() throws {
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        try Data("this is not a sqlite file".utf8).write(to: URL(fileURLWithPath: databasePath))

        do {
            let store = makeStore()
            store.record(snapshot(0.25, at: start))
            store.close()
        }
        // Reopen so the assertion is backed by the recreated file, not the first store's
        // memory tail, which is filled even when disk writes fail.
        let reopened = makeStore()
        XCTAssertEqual(reopened.recent(tool: .codex, windowID: window).map(\.usedRatio), [0.25])
        XCTAssertEqual(countRows(), 1)
    }

    func testNonCorruptionFailureDoesNotDeleteHistory() {
        do {
            let store = makeStore()
            store.record(snapshot(0.10, at: start.addingTimeInterval(-300)))
            store.close()
        }
        execute("""
            CREATE TABLE sentinel (value TEXT NOT NULL);
            INSERT INTO sentinel VALUES ('keep');
            CREATE TRIGGER reject_sample BEFORE INSERT ON samples
            BEGIN
                SELECT RAISE(ABORT, 'expected test constraint');
            END;
            """)

        do {
            let store = makeStore()
            store.record(snapshot(0.20, at: start))
            store.close()
        }

        XCTAssertEqual(countRows(), 1)
        var database: OpaquePointer?
        defer { sqlite3_close_v2(database) }
        XCTAssertEqual(sqlite3_open_v2(databasePath, &database, SQLITE_OPEN_READONLY, nil), SQLITE_OK)
        var statement: OpaquePointer?
        defer { sqlite3_finalize(statement) }
        XCTAssertEqual(sqlite3_prepare_v2(database, "SELECT COUNT(*) FROM sentinel", -1, &statement, nil), SQLITE_OK)
        XCTAssertEqual(sqlite3_step(statement), SQLITE_ROW)
        XCTAssertEqual(sqlite3_column_int64(statement, 0), 1)
    }

    func testClosedStoreRecordsAndReadsNothing() {
        let store = makeStore()
        store.close()
        store.record(snapshot(0.10, at: start))
        XCTAssertTrue(store.recent(tool: .codex, windowID: window).isEmpty)
        XCTAssertNil(store.weekdayProfile(tool: .codex, windowID: window))
    }

    // MARK: - Weekday profile (automatic pace model)

    // Monday 2026-08-03 10:00 UTC.
    private var monday: Date { start.addingTimeInterval(-2 * 3600) }

    func testProfileCreditsIncreasesToTheLocalWeekdayAndIgnoresDecreases() {
        let store = makeStore()
        recordAt(store, monday, 0.10)
        recordAt(store, monday.addingTimeInterval(2 * 3600), 0.30)             // +0.20 Monday
        recordAt(store, monday.addingTimeInterval(86_400), 0.30)               // Tuesday, no change
        recordAt(store, monday.addingTimeInterval(86_400 + 7200), 0.40)        // +0.10 Tuesday
        recordAt(store, monday.addingTimeInterval(2 * 86_400), 0.20)           // Wednesday: a reset, not consumption
        recordAt(store, monday.addingTimeInterval(2 * 86_400 + 7200), 0.50)    // +0.30 Wednesday

        let profile = store.weekdayProfile(tool: .codex, windowID: window)
        XCTAssertEqual(profile?.daysObserved, 3)
        assertWeights(profile, monday: 0.20, tuesday: 0.10, wednesday: 0.30)
        XCTAssertEqual(profile?.isSufficient, false)
        XCTAssertNil(store.weekdayProfile(tool: .codex, windowID: "somethingElse"))
        XCTAssertNil(store.weekdayProfile(tool: .claude, windowID: window))
    }

    func testProfileDropsAnIncreaseAcrossALongGap() {
        // A day of readings missing (the machine asleep): the consumption happened on days
        // never observed, so it is not credited to the day the readings resumed.
        let store = makeStore()
        let thursday = monday.addingTimeInterval(3 * 86_400 - 2 * 3600) // Thursday 08:00
        recordAt(store, thursday, 0.50)
        recordAt(store, thursday.addingTimeInterval(11 * 3600), 0.55)   // +0.05 Thursday
        recordAt(store, thursday.addingTimeInterval(36 * 3600), 0.90)   // Friday 20:00, 25h later: dropped

        let profile = store.weekdayProfile(tool: .codex, windowID: window)
        XCTAssertEqual(profile?.daysObserved, 2)
        assertWeights(profile, thursday: 0.05)
    }

    func testProfileSurvivesReopenWithoutDoubleCounting() {
        do {
            let store = makeStore()
            recordAt(store, monday, 0.10)
            recordAt(store, monday.addingTimeInterval(3600), 0.30)             // +0.20 Monday
            recordAt(store, monday.addingTimeInterval(86_400), 0.35)           // Tuesday, 23h gap: dropped
            recordAt(store, monday.addingTimeInterval(86_400 + 3600), 0.45)    // +0.10 Tuesday
            store.close()
        }
        let store = makeStore()
        let hydrated = store.weekdayProfile(tool: .codex, windowID: window)
        XCTAssertEqual(hydrated?.daysObserved, 2)
        assertWeights(hydrated, monday: 0.20, tuesday: 0.10)

        // A reading recorded after hydration adds exactly its own increase, never re-folding
        // what the database already gave.
        recordAt(store, monday.addingTimeInterval(86_400 + 7200), 0.50)        // +0.05 Tuesday
        let updated = store.weekdayProfile(tool: .codex, windowID: window)
        XCTAssertEqual(updated?.daysObserved, 2)
        assertWeights(updated, monday: 0.20, tuesday: 0.15)
    }

    func testProfileAveragesEachWeekdayOverTheTimesItWasSeen() {
        // The same 0.10 every day, but Monday observed twice and Tuesday once: a raw sum
        // would give Monday double weight and make an even spender look off pace.
        let store = makeStore()
        for offset in [0.0, 1, 7] {
            let day = monday.addingTimeInterval(offset * 86_400)
            recordAt(store, day, 0.10)
            recordAt(store, day.addingTimeInterval(3600), 0.20)
        }
        let profile = store.weekdayProfile(tool: .codex, windowID: window)
        XCTAssertEqual(profile?.daysObserved, 3)
        assertWeights(profile, monday: 0.10, tuesday: 0.10)
    }

    func testProfileCreditsUseAfterAResetButNotAJitteredRecompute() {
        let store = makeStore()
        let firstReset = monday.addingTimeInterval(3600)
        recordAt(store, monday, 0.80, reset: firstReset)
        // Same window, reset time jittered by seconds, percent recomputed down: not a reset.
        recordAt(store, monday.addingTimeInterval(1800), 0.75, reset: firstReset.addingTimeInterval(20))
        // Past the reset: the window rolled over, and the 0.05 shown was used since it.
        recordAt(store, monday.addingTimeInterval(7200), 0.05, reset: firstReset.addingTimeInterval(5 * 3600))

        assertWeights(store.weekdayProfile(tool: .codex, windowID: window), monday: 0.05)
    }

    func testProfilePairsTheFirstReadingAfterRestartWithTheLastStoredOne() {
        // Closed for eight hours — past the six-hour memory tail but inside the twelve-hour
        // gap — the first reading after reopening still credits its increase, exactly as the
        // next launch's fold of the same rows would.
        let early = monday.addingTimeInterval(-2 * 3600) // Monday 08:00
        do {
            let store = makeStore()
            recordAt(store, early, 0.10)
            store.close()
        }
        let store = makeStore()
        recordAt(store, early.addingTimeInterval(8 * 3600), 0.30)
        assertWeights(store.weekdayProfile(tool: .codex, windowID: window), monday: 0.20)
    }

    func testProfileCountsDaysInTheGivenZone() {
        // 23:30 UTC on Monday is Tuesday 08:30 in Seoul, so the increase lands on Tuesday
        // there and on Monday in UTC.
        let store = makeStore(timeZone: TimeZone(secondsFromGMT: 9 * 3600)!)
        let lateMondayUtc = monday.addingTimeInterval(13 * 3600) // Monday 23:00 UTC
        recordAt(store, lateMondayUtc, 0.10)
        recordAt(store, lateMondayUtc.addingTimeInterval(1800), 0.20)

        let profile = store.weekdayProfile(tool: .codex, windowID: window)
        assertWeights(profile, tuesday: 0.10)
        XCTAssertEqual(profile?.daysObserved, 1)
    }

    func testProfileIsSufficientAfterTwoWeeksOfReadings() {
        let store = makeStore()
        for offset in 0..<14 {
            let day = monday.addingTimeInterval(Double(offset) * 86_400)
            recordAt(store, day, 0.10, reset: day.addingTimeInterval(-3600))
            recordAt(store, day.addingTimeInterval(3600), 0.20, reset: day.addingTimeInterval(-3600))
        }
        let profile = store.weekdayProfile(tool: .codex, windowID: window)
        XCTAssertEqual(profile?.daysObserved, 14)
        XCTAssertEqual(profile?.isSufficient, true)
        assertWeights(profile, sunday: 0.10, monday: 0.10, tuesday: 0.10, wednesday: 0.10,
                      thursday: 0.10, friday: 0.10, saturday: 0.10)
    }
}
