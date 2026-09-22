using Gauge.Models;
using Microsoft.Data.Sqlite;

namespace Gauge.Services;

/// <summary>Receives live snapshots for historical recording.</summary>
public interface IUsageHistoryRecorder
{
    /// <summary>Records every window of a genuinely live snapshot.</summary>
    void Record(UsageSnapshot snapshot);
}

/// <summary>Serves recent recorded samples for trend/ETA computation.</summary>
public interface IUsageHistorySource
{
    /// <summary>
    /// Samples for one window over the trailing <paramref name="lookback"/>, oldest first.
    /// Empty when nothing was recorded.
    /// </summary>
    IReadOnlyList<UsageSample> GetRecent(string toolName, string windowKey, TimeSpan lookback);

    /// <summary>
    /// How one window's consumption has fallen across the local weekdays over the recent
    /// weeks (see <see cref="UsageWeekdayProfile"/>), for the automatic pace model. Null
    /// when nothing was recorded for the window.
    /// </summary>
    UsageWeekdayProfile? GetWeekdayProfile(string toolName, string windowKey) => null;
}

/// <summary>
/// Append-only history of live usage readings, stored as
/// <c>%APPDATA%\Gauge\usage-history.db</c> (SQLite, WAL).
///
/// WHY: every card value today is an instantaneous reading; burn-rate, ETA-to-exhaustion,
/// and any future trend display need the readings over time. This store is purely additive
/// analytics — <see cref="UsageCacheStore"/> remains the single source of truth for the
/// "current" state and rehydration, and a missing or corrupt history DB must never affect
/// startup or refreshes (it is deleted and recreated instead).
///
/// Only values Gauge itself computed are stored; no tokens or credentials ever touch this
/// file. The caller (coordinator) is responsible for recording only genuinely live
/// snapshots — ones whose <see cref="UsageSnapshot.CapturedAt"/> advanced — so a provider
/// re-serving its cached snapshot during a cooldown never produces duplicate rows; the
/// UNIQUE index is a second line of defense.
/// </summary>
public sealed class UsageHistoryStore : IUsageHistoryRecorder, IUsageHistorySource, IDisposable
{
    private const string FileName = "usage-history.db";
    private const int SqliteCorrupt = 11;
    private const int SqliteNotADatabase = 26;
    private static readonly TimeSpan Retention = TimeSpan.FromDays(90);
    private static readonly TimeSpan PruneInterval = TimeSpan.FromHours(24);

    // Reads for ETA/trends only ever need a short trailing span, so recent samples are
    // mirrored in memory and GetRecent never touches the database (it can be called on
    // the UI thread while a refresh is writing on the coordinator thread).
    private static readonly TimeSpan MemoryTailSpan = TimeSpan.FromHours(6);

    // The automatic pace model's weekday profile is folded from this much history at
    // hydration and then kept current from each recorded reading, so — like the tail — it
    // is served from memory. Four weeks sees every weekday four times; the profile's own
    // two-week sufficiency gate decides when it may shape a curve.
    private static readonly TimeSpan ProfileSpan = TimeSpan.FromDays(28);

    // A utilization increase is credited to a weekday only when the previous reading is
    // this recent. Across a longer gap (a laptop asleep over a weekend) the consumption
    // belongs to days that were never observed, and crediting it to the day the machine
    // woke would teach the profile a shape the user never had.
    private static readonly TimeSpan MaximumProfileGap = TimeSpan.FromHours(12);

    private readonly string _path;
    private readonly TimeProvider _time;
    private readonly TimeZoneInfo _zone;
    private readonly object _gate = new();
    private readonly Dictionary<(string Tool, string Key), List<UsageSample>> _tail = new();
    private readonly Dictionary<(string Tool, string Key), WeekdayAccumulator> _profiles = new();

    private SqliteConnection? _connection;
    private bool _tailHydrated;
    private DateTimeOffset _lastPrunedAt;
    private bool _disposed;

    /// <param name="zone">
    /// Zone whose calendar days the weekday profile counts; the machine's local zone by
    /// default, injectable so tests do not depend on where they run.
    /// </param>
    public UsageHistoryStore(string? directory = null, TimeProvider? time = null, TimeZoneInfo? zone = null)
    {
        _path = Path.Combine(directory ?? AppSettingsFile.DefaultDirectory, FileName);
        _time = time ?? TimeProvider.System;
        _zone = zone ?? TimeZoneInfo.Local;
    }

    public void Record(UsageSnapshot snapshot)
    {
        if (snapshot.Windows.Count == 0)
        {
            return;
        }

        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
            WithConnection(connection =>
            {
                using var transaction = connection.BeginTransaction();
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText =
                    """
                    INSERT OR IGNORE INTO samples
                        (tool, window_key, group_label, captured_at, used_ratio,
                         reset_time, used_tokens, limit_tokens)
                    VALUES ($tool, $key, $group, $captured, $ratio, $reset, $used, $limit)
                    """;
                var tool = command.Parameters.Add("$tool", SqliteType.Text);
                var key = command.Parameters.Add("$key", SqliteType.Text);
                var group = command.Parameters.Add("$group", SqliteType.Text);
                var captured = command.Parameters.Add("$captured", SqliteType.Integer);
                var ratio = command.Parameters.Add("$ratio", SqliteType.Real);
                var reset = command.Parameters.Add("$reset", SqliteType.Integer);
                var used = command.Parameters.Add("$used", SqliteType.Integer);
                var limit = command.Parameters.Add("$limit", SqliteType.Integer);

                foreach (var window in snapshot.Windows)
                {
                    tool.Value = snapshot.ToolName;
                    key.Value = window.Key;
                    group.Value = (object?)window.GroupLabel ?? DBNull.Value;
                    captured.Value = snapshot.CapturedAt.ToUnixTimeMilliseconds();
                    ratio.Value = window.UsedRatio;
                    reset.Value = (object?)window.ResetTime?.ToUnixTimeMilliseconds() ?? DBNull.Value;
                    used.Value = (object?)window.UsedTokens ?? DBNull.Value;
                    limit.Value = (object?)window.LimitTokens ?? DBNull.Value;
                    command.ExecuteNonQuery();
                }
                transaction.Commit();
            });

            AppendToTail(snapshot);
            PruneIfDue();
        }
    }

    public IReadOnlyList<UsageSample> GetRecent(string toolName, string windowKey, TimeSpan lookback)
    {
        var cutoff = _time.GetUtcNow() - lookback;
        lock (_gate)
        {
            if (_disposed)
            {
                return Array.Empty<UsageSample>();
            }
            HydrateTailIfNeeded();
            if (!_tail.TryGetValue((toolName, windowKey), out var samples))
            {
                return Array.Empty<UsageSample>();
            }
            return samples.Where(s => s.CapturedAt >= cutoff).ToList();
        }
    }

    public UsageWeekdayProfile? GetWeekdayProfile(string toolName, string windowKey)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return null;
            }
            HydrateTailIfNeeded();
            return _profiles.TryGetValue((toolName, windowKey), out var profile) ? profile.ToProfile() : null;
        }
    }

    /// <summary>
    /// Loads the trailing span from disk once, so later reads are memory-only. The weekday
    /// profiles are folded from the wider <see cref="ProfileSpan"/> in the same pass.
    /// </summary>
    private void HydrateTailIfNeeded()
    {
        if (_tailHydrated)
        {
            return;
        }
        _tailHydrated = true;

        var now = _time.GetUtcNow();
        var since = (now - MemoryTailSpan).ToUnixTimeMilliseconds();
        var profileSince = (now - ProfileSpan).ToUnixTimeMilliseconds();
        WithConnection(connection =>
        {
            using (var command = connection.CreateCommand())
            {
                command.CommandText =
                    """
                    SELECT tool, window_key, captured_at, used_ratio, reset_time FROM samples
                    WHERE captured_at >= $since ORDER BY captured_at
                    """;
                command.Parameters.AddWithValue("$since", since);
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    var key = (reader.GetString(0), reader.GetString(1));
                    if (!_tail.TryGetValue(key, out var list))
                    {
                        _tail[key] = list = new List<UsageSample>();
                    }
                    list.Add(new UsageSample(
                        DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(2)),
                        reader.GetDouble(3),
                        reader.IsDBNull(4) ? null : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(4))));
                }
            }

            // Ordered per window so consecutive rows are consecutive readings; the fold
            // credits each increase to the local weekday it was observed on.
            using (var command = connection.CreateCommand())
            {
                command.CommandText =
                    """
                    SELECT tool, window_key, captured_at, used_ratio FROM samples
                    WHERE captured_at >= $since ORDER BY tool, window_key, captured_at
                    """;
                command.Parameters.AddWithValue("$since", profileSince);
                using var reader = command.ExecuteReader();
                (string, string)? currentKey = null;
                UsageSample? previous = null;
                while (reader.Read())
                {
                    var key = (reader.GetString(0), reader.GetString(1));
                    if (key != currentKey)
                    {
                        currentKey = key;
                        previous = null;
                    }
                    var sample = new UsageSample(DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(2)), reader.GetDouble(3));
                    Accumulator(key).Add(previous, sample, _zone);
                    previous = sample;
                }
            }
        });
    }

    private void AppendToTail(UsageSnapshot snapshot)
    {
        HydrateTailIfNeeded();
        var cutoff = _time.GetUtcNow() - MemoryTailSpan;
        foreach (var window in snapshot.Windows)
        {
            var key = (snapshot.ToolName, window.Key);
            if (!_tail.TryGetValue(key, out var list))
            {
                _tail[key] = list = new List<UsageSample>();
            }
            // Mirror the DB's UNIQUE(tool, window_key, captured_at) so a re-recorded
            // snapshot never duplicates the in-memory tail either.
            if (list.Count > 0 && list[^1].CapturedAt >= snapshot.CapturedAt)
            {
                continue;
            }
            var sample = new UsageSample(snapshot.CapturedAt, window.UsedRatio, window.ResetTime);
            // The tail's last reading is the previous one for the profile too. It is still
            // present after a gap longer than the tail span, because the cutoff prune below
            // runs after the add using the previous call's clock; the accumulator's own
            // twelve-hour gate — not the tail span — decides whether the increase is
            // credited, exactly as in the hydration fold. Keep the prune after the add.
            Accumulator(key).Add(list.Count > 0 ? list[^1] : null, sample, _zone);
            list.Add(sample);
            list.RemoveAll(s => s.CapturedAt < cutoff);
        }
    }

    private WeekdayAccumulator Accumulator((string Tool, string Key) key)
    {
        if (!_profiles.TryGetValue(key, out var accumulator))
        {
            _profiles[key] = accumulator = new WeekdayAccumulator();
        }
        return accumulator;
    }

    /// <summary>
    /// One window's running weekday profile: the summed utilization increases per local
    /// weekday and the distinct local dates seen. Decreases are never counted — a reset
    /// or a provider recomputing its headline percent is not consumption — and an increase
    /// across more than <see cref="MaximumProfileGap"/> is dropped rather than credited to
    /// the day the readings resumed.
    /// </summary>
    private sealed class WeekdayAccumulator
    {
        private readonly double[] _weights = new double[7];
        private readonly HashSet<DateOnly> _days = new();

        public void Add(UsageSample? previous, UsageSample current, TimeZoneInfo zone)
        {
            var local = TimeZoneInfo.ConvertTime(current.CapturedAt, zone);
            _days.Add(DateOnly.FromDateTime(local.Date));
            if (previous is not { } last) return;
            var delta = current.UsedRatio - last.UsedRatio;
            if (delta <= 0 || !double.IsFinite(delta)) return;
            if (current.CapturedAt - last.CapturedAt > MaximumProfileGap) return;
            _weights[(int)local.DayOfWeek] += delta;
        }

        public UsageWeekdayProfile ToProfile() => new(_weights.ToArray(), _days.Count);
    }

    private void PruneIfDue()
    {
        var now = _time.GetUtcNow();
        if (_lastPrunedAt != default && now - _lastPrunedAt < PruneInterval)
        {
            return;
        }
        _lastPrunedAt = now;
        var cutoff = (now - Retention).ToUnixTimeMilliseconds();
        WithConnection(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM samples WHERE captured_at < $cutoff";
            command.Parameters.AddWithValue("$cutoff", cutoff);
            command.ExecuteNonQuery();
        });
    }

    /// <summary>
    /// Runs one operation against the open connection. A corrupt database is deleted and
    /// recreated once (history is expendable analytics); any further failure is swallowed
    /// so recording can never break a refresh cycle.
    /// </summary>
    private void WithConnection(Action<SqliteConnection> action)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                action(EnsureConnection());
                return;
            }
            catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                // Neutral wording on purpose: this runs for every caller — recording,
                // hydrating and pruning — so naming one of them would misdirect triage.
                DiagnosticsLog.Write("history", $"Usage history access failed: {ex.GetType().Name}");
                CloseConnection();
                if (attempt == 0 && ex is SqliteException sqliteError && IsCorruption(sqliteError))
                {
                    TryDeleteDatabase();
                    continue;
                }
                return;
            }
        }
    }

    private SqliteConnection EnsureConnection()
    {
        if (_connection is not null)
        {
            return _connection;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _path,
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ToString());
        try
        {
            connection.Open();
            using (var command = connection.CreateCommand())
            {
                command.CommandText =
                    """
                    PRAGMA journal_mode=WAL;
                    CREATE TABLE IF NOT EXISTS samples (
                        tool         TEXT    NOT NULL,
                        window_key   TEXT    NOT NULL,
                        group_label  TEXT    NULL,
                        captured_at  INTEGER NOT NULL,
                        used_ratio   REAL    NOT NULL,
                        reset_time   INTEGER NULL,
                        used_tokens  INTEGER NULL,
                        limit_tokens INTEGER NULL
                    );
                    CREATE UNIQUE INDEX IF NOT EXISTS ix_samples_identity
                        ON samples (tool, window_key, captured_at);
                    """;
                command.ExecuteNonQuery();
            }
            // Rows are keyed by display name, so a renamed tool carries its history across
            // the rename. OR IGNORE keeps a row already present under the new name; the
            // leftover old-name rows (if any collided) are then dropped.
            foreach (var (oldName, newName) in ToolCatalog.RenamedDisplayNames)
            {
                using var migrate = connection.CreateCommand();
                migrate.CommandText =
                    """
                    UPDATE OR IGNORE samples SET tool = $new WHERE tool = $old;
                    DELETE FROM samples WHERE tool = $old;
                    """;
                migrate.Parameters.AddWithValue("$new", newName);
                migrate.Parameters.AddWithValue("$old", oldName);
                migrate.ExecuteNonQuery();
            }
            _connection = connection;
            return connection;
        }
        catch
        {
            // Ownership transfers to _connection only after initialization succeeds.
            // Until then this local handle must be closed before corrupt-file recovery
            // can delete the database on Windows.
            connection.Dispose();
            throw;
        }
    }

    private static bool IsCorruption(SqliteException error)
        => error.SqliteErrorCode is SqliteCorrupt or SqliteNotADatabase;

    private void CloseConnection()
    {
        try
        {
            _connection?.Dispose();
        }
        catch
        {
            // ignore
        }
        _connection = null;
    }

    private void TryDeleteDatabase()
    {
        // SQLite must not hold the file while it is deleted; pooled handles keep it open.
        SqliteConnection.ClearAllPools();
        foreach (var path in new[] { _path, _path + "-wal", _path + "-shm" })
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                DiagnosticsLog.Write("history", $"Usage history reset failed: {ex.GetType().Name}");
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            CloseConnection();
        }
    }
}
