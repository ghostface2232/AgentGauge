using System.Globalization;
using Gauge.Models;
using Microsoft.Data.Sqlite;

namespace Gauge.Services.ApiCost;

/// <summary>
/// Reads per-call token usage from Antigravity's conversation databases
/// (<c>~/.gemini/antigravity*/conversations/&lt;id&gt;.db</c>). Each model call is one
/// <c>gen_metadata</c> row holding an undocumented protobuf, so the field numbers below were
/// inferred from live data and may change with an app update; a row that does not decode is
/// skipped, never guessed. Observed per call (field 1 of the blob, sub-message 4): field 2 =
/// uncached prompt tokens, field 5 = prompt tokens served from cache (it tracks the previous
/// call's prompt total, though parallel calls blur that), field 3 = output tokens including thinking (field 9 + field 10).
/// Only <c>gen_metadata</c> and step timestamps are read, never conversation content.
/// </summary>
public static class AntigravityConversationReader
{
    // Claude models routed through Antigravity carry a reasoning-effort suffix
    // ("claude-opus-5-5-medium") that is not part of the API model name.
    private static readonly System.Text.RegularExpressions.Regex EffortSuffix =
        new(@"-(minimal|low|medium|high|xhigh|max)$", System.Text.RegularExpressions.RegexOptions.Compiled);

    private const int UsageMessage = 4, ModelField = 19, Attributes = 20;
    private const int UncachedPrompt = 2, Output = 3, CachedPrompt = 5;

    /// <summary>The conversation's own id, stable for the life of the file.</summary>
    public static string? ReadIdentity(string path)
    {
        try
        {
            using var connection = Open(path);
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT cascade_id FROM trajectory_meta LIMIT 1";
            return command.ExecuteScalar() as string;
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Calls with an index above <paramref name="afterIdx"/>, oldest first, and the highest
    /// index seen. Null when the database could not be read right now (the IDE holding it).
    /// </summary>
    public static (List<UsageLogRecord> Records, long LastIdx)? Read(string path, long afterIdx, DateTimeOffset fallback)
    {
        try
        {
            using var connection = Open(path);
            var records = new List<UsageLogRecord>();
            var last = afterIdx;
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT idx, data FROM gen_metadata WHERE idx > $after ORDER BY idx";
            command.Parameters.AddWithValue("$after", afterIdx);
            var rows = new List<(long Idx, byte[] Data)>();
            using (var reader = command.ExecuteReader())
            {
                while (reader.Read()) rows.Add((reader.GetInt64(0), (byte[])reader["data"]));
            }
            foreach (var (idx, data) in rows)
            {
                last = idx;
                if (Decode(data) is not { } call) continue;
                var at = call.LastStep is { } step ? StepTime(connection, step) ?? fallback : fallback;
                records.Add(new UsageLogRecord(at, call.Model, call.Tokens, call.RequestId));
            }
            return (records, last);
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
        {
            DiagnosticsLog.Write("apicost", $"Antigravity read failed: {ex.GetType().Name}");
            return null;
        }
    }

    private sealed record Call(string Model, TokenTotals Tokens, string? RequestId, long? LastStep);

    private static Call? Decode(byte[] blob)
    {
        var top = Proto.Parse(blob);
        if (top.Message(1) is not { } meta || meta.Message(UsageMessage) is not { } usage) return null;
        if (meta.String(ModelField) is not { Length: > 0 } logged) return null;
        var model = EffortSuffix.Replace(logged, "");
        var prompt = usage.Varint(UncachedPrompt);
        var cached = usage.Varint(CachedPrompt) ?? 0;
        var output = usage.Varint(Output);
        if (prompt is null || output is null) return null;

        string? requestId = null;
        long? lastStep = null;
        foreach (var attribute in meta.Messages(Attributes))
        {
            var value = attribute.String(2);
            switch (attribute.String(1))
            {
                case "request_id": requestId = value; break;
                case "last_step_index" when long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var step): lastStep = step; break;
            }
        }
        return new Call(model, new TokenTotals(prompt.Value, cached, 0, 0, output.Value), requestId, lastStep);
    }

    // A step's metadata starts with a protobuf Timestamp (seconds, nanos) of its creation.
    private static DateTimeOffset? StepTime(SqliteConnection connection, long step)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT metadata FROM steps WHERE idx = $idx";
        command.Parameters.AddWithValue("$idx", step);
        if (command.ExecuteScalar() is not byte[] metadata) return null;
        return Proto.Parse(metadata).Message(1)?.Varint(1) is { } seconds and > 0
            ? DateTimeOffset.FromUnixTimeSeconds(seconds) : null;
    }

    // Read-only so the IDE's own writes are never contended. Even a read-only open of a WAL
    // database creates its -shm and -wal files when none exist, which would leave files
    // behind in Antigravity's data folder. With no -wal the IDE is not running and the main
    // file is complete, so it is opened immutable; while the IDE runs the WAL is honoured.
    private static SqliteConnection Open(string path)
    {
        var source = File.Exists(path + "-wal") ? path : new Uri(path).AbsoluteUri + "?immutable=1";
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = source,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        connection.Open();
        return connection;
    }

    /// <summary>Just enough protobuf wire parsing to pick fields out of a blob.</summary>
    private sealed class Proto
    {
        private readonly Dictionary<int, List<object>> _fields = new();

        public static Proto Parse(byte[] bytes)
        {
            var message = new Proto();
            var i = 0;
            try
            {
                while (i < bytes.Length)
                {
                    var key = Varint(bytes, ref i);
                    var field = (int)(key >> 3);
                    object value;
                    switch ((int)(key & 7))
                    {
                        case 0: value = Varint(bytes, ref i); break;
                        case 1: i += 8; continue;
                        case 5: i += 4; continue;
                        case 2:
                            var length = (int)Varint(bytes, ref i);
                            value = bytes.AsMemory(i, length).ToArray();
                            i += length;
                            break;
                        default: return message;
                    }
                    if (!message._fields.TryGetValue(field, out var list)) message._fields[field] = list = [];
                    list.Add(value);
                }
            }
            catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentOutOfRangeException or OverflowException)
            {
                // A truncated or unexpected blob keeps whatever fields decoded before it.
            }
            return message;
        }

        public long? Varint(int field) => _fields.TryGetValue(field, out var l) && l[0] is ulong v ? (long)v : null;

        public string? String(int field)
            => _fields.TryGetValue(field, out var l) && l[0] is byte[] b ? System.Text.Encoding.UTF8.GetString(b) : null;

        public Proto? Message(int field) => _fields.TryGetValue(field, out var l) && l[0] is byte[] b ? Parse(b) : null;

        public IEnumerable<Proto> Messages(int field)
            => _fields.TryGetValue(field, out var l) ? l.OfType<byte[]>().Select(Parse) : [];

        private static ulong Varint(byte[] bytes, ref int i)
        {
            ulong result = 0;
            for (var shift = 0; shift < 64; shift += 7)
            {
                var b = bytes[i++];
                result |= (ulong)(b & 0x7f) << shift;
                if ((b & 0x80) == 0) return result;
            }
            throw new OverflowException();
        }
    }
}
