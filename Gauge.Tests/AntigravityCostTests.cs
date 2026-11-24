using Gauge.Models;
using Gauge.Services.ApiCost;
using Microsoft.Data.Sqlite;

namespace Gauge.Tests;

/// <summary>
/// The Antigravity conversation reader and its scanner branch, against a fixture database
/// shaped like the real one (trajectory_meta, gen_metadata protobuf blobs, steps).
/// </summary>
public sealed class AntigravityCostTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "GaugeAgyCost_" + Guid.NewGuid().ToString("N"));
    private string Conversations => Path.Combine(_dir, "conversations");
    private string Database => Path.Combine(Conversations, "c1.db");
    private string LedgerPath => Path.Combine(_dir, "ledger.json");

    [Fact]
    public void ReadsTokensModelAndDayFromEachCall()
    {
        CreateConversation(Call(0, "gemini-4-argon-trial", prompt: 10_690, cached: 0, output: 465, stepSeconds: StepSeconds));
        var (records, last) = AntigravityConversationReader.Read(Database, -1, DateTimeOffset.UnixEpoch)!.Value;
        var record = Assert.Single(records);
        Assert.Equal(0, last);
        Assert.Equal("gemini-4-argon-trial", record.Model);
        Assert.Equal(new TokenTotals(10_690, 0, 0, 0, 465), record.Tokens);
        Assert.Equal("conv-0", record.Key);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(StepSeconds), record.Timestamp);
    }

    [Fact]
    public void ScanCountsACallOnceAndResumesAfterTheLastIndex()
    {
        CreateConversation(Call(0, "gemini-3.8-flash-n", 1_000_000, 0, 100_000, StepSeconds));
        var scanner = new ApiCostScanner(LedgerPath,
            [new ApiCostScanner.LogSource("Antigravity", ApiCostScanner.LogKind.Antigravity, [Conversations])],
            new FixedTime(DateTimeOffset.FromUnixTimeSeconds(StepSeconds)), TimeZoneInfo.Utc);
        var tools = new HashSet<string> { "Antigravity" };

        var first = Assert.Single(scanner.Scan(tools));
        // 1M prompt at 0.75 + 100K output at 3.75.
        Assert.Equal(0.75m + 0.375m, first.CostUsd);
        Assert.Equal(first.CostUsd, Assert.Single(scanner.Scan(tools)).CostUsd);

        Append(Call(1, "gemini-3.8-flash-n", 0, 1_000_000, 0, StepSeconds));
        var third = Assert.Single(scanner.Scan(tools));
        Assert.Equal(0.75m + 0.375m + 0.075m, third.CostUsd);
    }

    [Fact]
    public void AnUnreadableBlobIsSkippedNotCounted()
    {
        CreateConversation(new CallRow(0, [0xff, 0xff, 0xff], null));
        Assert.Empty(AntigravityConversationReader.Read(Database, -1, DateTimeOffset.UnixEpoch)!.Value.Records);
    }

    // ── Fixture ──────────────────────────────────────────────────────────────

    private const long StepSeconds = 1_792_667_640;

    private sealed record CallRow(long Idx, byte[] Blob, long? StepIdx);

    private static CallRow Call(long idx, string model, long prompt, long cached, long output, long stepSeconds)
    {
        var usage = Join(Varint(2, prompt), Varint(3, output), cached > 0 ? Varint(5, cached) : []);
        var meta = Join(
            Message(4, usage),
            String(19, model),
            Message(20, Join(String(1, "request_id"), String(2, $"conv-{idx}"))),
            Message(20, Join(String(1, "last_step_index"), String(2, idx.ToString(System.Globalization.CultureInfo.InvariantCulture)))));
        return new CallRow(idx, Message(1, meta), idx);
    }

    private void CreateConversation(CallRow first)
    {
        Directory.CreateDirectory(Conversations);
        using var connection = Connect();
        Exec(connection, "CREATE TABLE trajectory_meta (cascade_id text); INSERT INTO trajectory_meta VALUES ('cascade-1');"
            + "CREATE TABLE gen_metadata (idx integer PRIMARY KEY, data blob);"
            + "CREATE TABLE steps (idx integer PRIMARY KEY, metadata blob);");
        Insert(connection, first);
    }

    private void Append(CallRow row)
    {
        using var connection = Connect();
        Insert(connection, row);
        // Bump the file so change detection sees the append.
        File.SetLastWriteTimeUtc(Database, File.GetLastWriteTimeUtc(Database).AddMinutes(1));
    }

    private static void Insert(SqliteConnection connection, CallRow row)
    {
        using var gen = connection.CreateCommand();
        gen.CommandText = "INSERT INTO gen_metadata VALUES ($i, $d)";
        gen.Parameters.AddWithValue("$i", row.Idx);
        gen.Parameters.AddWithValue("$d", row.Blob);
        gen.ExecuteNonQuery();
        using var step = connection.CreateCommand();
        step.CommandText = "INSERT INTO steps VALUES ($i, $m)";
        step.Parameters.AddWithValue("$i", row.Idx);
        step.Parameters.AddWithValue("$m", Message(1, Varint(1, StepSeconds)));
        step.ExecuteNonQuery();
    }

    private SqliteConnection Connect()
    {
        var connection = new SqliteConnection($"Data Source={Database};Pooling=False");
        connection.Open();
        return connection;
    }

    private static void Exec(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static byte[] Join(params byte[][] parts) => parts.SelectMany(p => p).ToArray();

    private static byte[] Varint(int field, long value) => Join(Raw((ulong)field << 3), Raw((ulong)value));

    private static byte[] String(int field, string value)
        => Message(field, System.Text.Encoding.UTF8.GetBytes(value));

    private static byte[] Message(int field, byte[] body) => Join(Raw(((ulong)field << 3) | 2), Raw((ulong)body.Length), body);

    private static byte[] Raw(ulong value)
    {
        var bytes = new List<byte>();
        while (value >= 0x80)
        {
            bytes.Add((byte)(value | 0x80));
            value >>= 7;
        }
        bytes.Add((byte)value);
        return [.. bytes];
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }
}
