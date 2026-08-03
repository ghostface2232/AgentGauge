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
        store.Record(Snapshot("Claude Code", 0.90, _time.Now));

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
