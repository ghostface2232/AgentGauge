using Gauge.Models;
using Gauge.Services;
using Microsoft.Data.Sqlite;

namespace Gauge.Tests;

/// <summary>
/// The append-only usage history: recording, duplicate suppression, lookback reads,
/// retention pruning, and corrupt-database self-healing.
/// </summary>
public sealed class UsageHistoryStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "GaugeHistoryTest_" + Guid.NewGuid().ToString("N"));
    private readonly MutableTime _time = new(new DateTimeOffset(2026, 8, 3, 12, 0, 0, TimeSpan.Zero));

    [Fact]
    public void RecordThenGetRecentReturnsSamplesOldestFirst()
    {
        using var store = new UsageHistoryStore(_dir, _time);
        store.Record(Snapshot("Codex", 0.10, _time.Now.AddMinutes(-10)));
        store.Record(Snapshot("Codex", 0.20, _time.Now.AddMinutes(-5)));
        store.Record(Snapshot("Codex", 0.30, _time.Now));

        var samples = store.GetRecent("Codex", UsageWindowType.FiveHour.ToString(), TimeSpan.FromHours(1));

        Assert.Equal(new[] { 0.10, 0.20, 0.30 }, samples.Select(s => s.UsedRatio));
        Assert.True(samples[0].CapturedAt < samples[^1].CapturedAt);
    }

    [Fact]
    public void DuplicateCaptureTimeIsRecordedOnce()
    {
        using var store = new UsageHistoryStore(_dir, _time);
        var snapshot = Snapshot("Codex", 0.10, _time.Now);
        store.Record(snapshot);
        store.Record(snapshot);

        var samples = store.GetRecent("Codex", UsageWindowType.FiveHour.ToString(), TimeSpan.FromHours(1));
        Assert.Single(samples);
        Assert.Equal(1, CountRows());
    }

    [Fact]
    public void GetRecentFiltersByLookbackAndKey()
    {
        using var store = new UsageHistoryStore(_dir, _time);
        store.Record(Snapshot("Codex", 0.10, _time.Now.AddHours(-3)));
        store.Record(Snapshot("Codex", 0.20, _time.Now));
        store.Record(Snapshot("Claude", 0.90, _time.Now));

        var samples = store.GetRecent("Codex", UsageWindowType.FiveHour.ToString(), TimeSpan.FromHours(1));
        var ratio = Assert.Single(samples).UsedRatio;
        Assert.Equal(0.20, ratio, 3);
        Assert.Empty(store.GetRecent("Codex", "SomethingElse", TimeSpan.FromHours(1)));
    }

    [Fact]
    public void RecentSamplesSurviveReopen()
    {
        using (var store = new UsageHistoryStore(_dir, _time))
        {
            store.Record(Snapshot("Codex", 0.40, _time.Now.AddMinutes(-2)));
        }

        using var reopened = new UsageHistoryStore(_dir, _time);
        var samples = reopened.GetRecent("Codex", UsageWindowType.FiveHour.ToString(), TimeSpan.FromHours(1));
        var ratio = Assert.Single(samples).UsedRatio;
        Assert.Equal(0.40, ratio, 3);
    }

    [Fact]
    public void SamplesCarryTheReportedResetTime()
    {
        // Cycle-boundary detection reads this off the samples, so it has to survive both
        // the in-memory tail and the hydration a later session starts from.
        var captured = _time.Now.AddMinutes(-2);
        using (var store = new UsageHistoryStore(_dir, _time))
        {
            store.Record(Snapshot("Codex", 0.40, captured));
            Assert.Equal(captured.AddHours(2), Recent(store));
        }

        using var reopened = new UsageHistoryStore(_dir, _time);
        Assert.Equal(captured.AddHours(2), Recent(reopened));

        static DateTimeOffset? Recent(UsageHistoryStore store) => Assert.Single(
            store.GetRecent("Codex", UsageWindowType.FiveHour.ToString(), TimeSpan.FromHours(1))).ResetTime;
    }

    [Fact]
    public void SamplesOlderThanRetentionArePrunedOnLaterRecord()
    {
        using var store = new UsageHistoryStore(_dir, _time);
        var old = _time.Now;
        store.Record(Snapshot("Codex", 0.50, old));

        // 100 days later the next record triggers the daily prune, which drops the old row.
        _time.Now = old.AddDays(100);
        store.Record(Snapshot("Codex", 0.10, _time.Now));

        Assert.Equal(1, CountRows());
    }

    [Fact]
    public void CorruptDatabaseIsRecreatedAndRecordingContinues()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "usage-history.db"), "this is not a sqlite file");

        using (var store = new UsageHistoryStore(_dir, _time))
        {
            store.Record(Snapshot("Codex", 0.25, _time.Now));
        }

        // Reopen so this assertion is backed by the recreated SQLite file rather than
        // the first store's in-memory tail, which is populated even when disk writes fail.
        using var reopened = new UsageHistoryStore(_dir, _time);
        var samples = reopened.GetRecent("Codex", UsageWindowType.FiveHour.ToString(), TimeSpan.FromHours(1));
        var ratio = Assert.Single(samples).UsedRatio;
        Assert.Equal(0.25, ratio, 3);
        Assert.Equal(1, CountRows());
    }

    [Fact]
    public void NonCorruptionSqliteFailureDoesNotDeleteHistory()
    {
        using (var store = new UsageHistoryStore(_dir, _time))
        {
            store.Record(Snapshot("Codex", 0.10, _time.Now.AddMinutes(-5)));
        }

        using (var connection = OpenDatabase())
        {
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                CREATE TABLE sentinel (value TEXT NOT NULL);
                INSERT INTO sentinel VALUES ('keep');
                CREATE TRIGGER reject_sample BEFORE INSERT ON samples
                BEGIN
                    SELECT RAISE(ABORT, 'expected test constraint');
                END;
                """;
            command.ExecuteNonQuery();
        }

        using (var store = new UsageHistoryStore(_dir, _time))
        {
            store.Record(Snapshot("Codex", 0.20, _time.Now));
        }

        using var verify = OpenDatabase();
        using var countSentinel = verify.CreateCommand();
        countSentinel.CommandText = "SELECT COUNT(*) FROM sentinel WHERE value = 'keep'";
        Assert.Equal(1L, (long)countSentinel.ExecuteScalar()!);

        using var countSamples = verify.CreateCommand();
        countSamples.CommandText = "SELECT COUNT(*) FROM samples";
        Assert.Equal(1L, (long)countSamples.ExecuteScalar()!);
    }

    private long CountRows()
    {
        using var connection = OpenDatabase(SqliteOpenMode.ReadOnly);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM samples";
        return (long)command.ExecuteScalar()!;
    }

    private SqliteConnection OpenDatabase(SqliteOpenMode mode = SqliteOpenMode.ReadWrite)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(_dir, "usage-history.db"),
            Mode = mode,
        }.ToString());
        connection.Open();
        return connection;
    }

    private static UsageSnapshot Snapshot(string tool, double ratio, DateTimeOffset capturedAt) => new()
    {
        ToolName = tool,
        CapturedAt = capturedAt,
        Windows = new[]
        {
            new UsageWindow
            {
                Type = UsageWindowType.FiveHour,
                Label = "5h",
                UsedRatio = ratio,
                ResetTime = capturedAt.AddHours(2),
            },
        },
    };

    // ── Weekday profile (automatic pace model) ──────────────────────────────────

    [Fact]
    public void WeekdayProfileCreditsIncreasesToTheLocalWeekdayAndIgnoresDecreases()
    {
        using var store = new UsageHistoryStore(_dir, _time, TimeZoneInfo.Utc);
        var monday = new DateTimeOffset(2026, 8, 3, 10, 0, 0, TimeSpan.Zero);
        RecordAt(store, monday, 0.10);
        RecordAt(store, monday.AddHours(2), 0.30);                  // +0.20 Monday
        RecordAt(store, monday.AddDays(1), 0.30);                   // Tuesday, no change
        RecordAt(store, monday.AddDays(1).AddHours(2), 0.40);       // +0.10 Tuesday
        RecordAt(store, monday.AddDays(2), 0.20);                   // Wednesday: a reset, not consumption
        RecordAt(store, monday.AddDays(2).AddHours(2), 0.50);       // +0.30 Wednesday

        var profile = store.GetWeekdayProfile("Codex", UsageWindowType.FiveHour.ToString());

        Assert.NotNull(profile);
        Assert.Equal(3, profile!.DaysObserved);
        AssertWeights(profile, monday: 0.20, tuesday: 0.10, wednesday: 0.30);
        Assert.False(profile.IsSufficient);
        Assert.Null(store.GetWeekdayProfile("Codex", "SomethingElse"));
        Assert.Null(store.GetWeekdayProfile("Claude", UsageWindowType.FiveHour.ToString()));
    }

    [Fact]
    public void WeekdayProfileDropsAnIncreaseAcrossALongGap()
    {
        // A day of readings missing (the machine asleep): the consumption happened on days
        // never observed, so it is not credited to the day the readings resumed.
        using var store = new UsageHistoryStore(_dir, _time, TimeZoneInfo.Utc);
        var thursday = new DateTimeOffset(2026, 8, 6, 8, 0, 0, TimeSpan.Zero);
        RecordAt(store, thursday, 0.50);
        RecordAt(store, thursday.AddHours(11), 0.55);   // +0.05 Thursday (inside the gap)
        RecordAt(store, thursday.AddHours(36), 0.90);   // Friday 20:00, 25h later: dropped

        var profile = store.GetWeekdayProfile("Codex", UsageWindowType.FiveHour.ToString())!;
        Assert.Equal(2, profile.DaysObserved);
        AssertWeights(profile, thursday: 0.05);
    }

    [Fact]
    public void WeekdayProfileSurvivesReopenWithoutDoubleCounting()
    {
        var monday = new DateTimeOffset(2026, 8, 3, 10, 0, 0, TimeSpan.Zero);
        using (var store = new UsageHistoryStore(_dir, _time, TimeZoneInfo.Utc))
        {
            RecordAt(store, monday, 0.10);
            RecordAt(store, monday.AddHours(1), 0.30);              // +0.20 Monday
            RecordAt(store, monday.AddDays(1), 0.35);               // Tuesday, 23h gap: dropped
            RecordAt(store, monday.AddDays(1).AddHours(1), 0.45);   // +0.10 Tuesday
        }

        using (var store = new UsageHistoryStore(_dir, _time, TimeZoneInfo.Utc))
        {
            // Hydrated from the database: the same shape as before the restart.
            var hydrated = store.GetWeekdayProfile("Codex", UsageWindowType.FiveHour.ToString())!;
            Assert.Equal(2, hydrated.DaysObserved);
            AssertWeights(hydrated, monday: 0.20, tuesday: 0.10);

            // A reading recorded after hydration adds exactly its own increase — the delta
            // to the tail's last sample — never re-folding what the database already gave.
            RecordAt(store, monday.AddDays(1).AddHours(2), 0.50);  // +0.05 Tuesday
            var updated = store.GetWeekdayProfile("Codex", UsageWindowType.FiveHour.ToString())!;
            Assert.Equal(2, updated.DaysObserved);
            AssertWeights(updated, monday: 0.20, tuesday: 0.15);
        }
    }

    [Fact]
    public void WeekdayProfileCountsDaysInTheGivenZone()
    {
        // 23:30 UTC on Monday is Tuesday 08:30 in Seoul, so the increase lands on Tuesday
        // there and on Monday in UTC.
        var seoul = TimeZoneInfo.CreateCustomTimeZone("KST", TimeSpan.FromHours(9), "KST", "KST");
        var lateMondayUtc = new DateTimeOffset(2026, 8, 3, 23, 0, 0, TimeSpan.Zero);
        using var store = new UsageHistoryStore(_dir, _time, seoul);
        RecordAt(store, lateMondayUtc, 0.10);
        RecordAt(store, lateMondayUtc.AddMinutes(30), 0.20);

        var profile = store.GetWeekdayProfile("Codex", UsageWindowType.FiveHour.ToString())!;
        AssertWeights(profile, tuesday: 0.10);
        Assert.Equal(1, profile.DaysObserved);
    }

    private void RecordAt(UsageHistoryStore store, DateTimeOffset capturedAt, double ratio)
    {
        _time.Now = capturedAt;
        store.Record(Snapshot("Codex", ratio, capturedAt));
    }

    private static void AssertWeights(UsageWeekdayProfile profile,
        double sunday = 0, double monday = 0, double tuesday = 0, double wednesday = 0,
        double thursday = 0, double friday = 0, double saturday = 0)
    {
        var expected = new[] { sunday, monday, tuesday, wednesday, thursday, friday, saturday };
        Assert.Equal(7, profile.Weights.Count);
        for (var day = 0; day < 7; day++)
        {
            Assert.Equal(expected[day], profile.Weights[day], 6);
        }
    }

    private sealed class MutableTime(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
        catch { /* best effort */ }
    }
}
