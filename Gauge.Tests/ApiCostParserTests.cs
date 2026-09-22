using Gauge.Models;
using Gauge.Services.ApiCost;

namespace Gauge.Tests;

/// <summary>
/// The two session-log readers behind the API-cost estimate: which lines count, how the
/// token classes normalize to one shape, and the identity that de-duplicates a response
/// written more than once.
/// </summary>
public sealed class ApiCostParserTests
{
    // ── Claude Code ──────────────────────────────────────────────────────────

    private static string ClaudeLine(
        string usage, string model = "claude-opus-5", string? stop = "\"end_turn\"",
        string id = "msg_1", string? requestId = "req_1", string timestamp = "2026-09-02T06:59:42.746Z")
        => "{\"type\":\"assistant\",\"timestamp\":\"" + timestamp + "\",\"sessionId\":\"s1\""
           + (requestId is null ? "" : ",\"requestId\":\"" + requestId + "\"")
           + ",\"message\":{\"id\":\"" + id + "\",\"model\":\"" + model + "\",\"stop_reason\":" + (stop ?? "null")
           + ",\"content\":[{\"type\":\"text\",\"text\":\"hello (\\\"usage\\\")\"}],\"usage\":" + usage + "}}";

    [Fact]
    public void ClaudeReadsEveryTokenClassAndTheResponseIdentity()
    {
        var line = ClaudeLine("""{"input_tokens":2,"cache_creation_input_tokens":30798,"cache_read_input_tokens":42801,"output_tokens":173}""");

        Assert.True(ClaudeUsageLogParser.TryParse(line, out var record));
        Assert.Equal("claude-opus-5", record.Model);
        Assert.Equal(new TokenTotals(2, 42801, 30798, 0, 173), record.Tokens);
        Assert.Equal("claude:msg_1:req_1", record.Key);
        Assert.Equal(new DateTimeOffset(2026, 9, 2, 6, 59, 42, 746, TimeSpan.Zero), record.Timestamp);
    }

    [Fact]
    public void ClaudeSplitsTheOneHourCacheSliceOutOfTheWriteCount()
    {
        var line = ClaudeLine("""{"input_tokens":10,"cache_creation_input_tokens":1000,"cache_read_input_tokens":0,"output_tokens":5,"cache_creation":{"ephemeral_5m_input_tokens":400,"ephemeral_1h_input_tokens":600}}""");
        Assert.True(ClaudeUsageLogParser.TryParse(line, out var record));
        Assert.Equal(new TokenTotals(10, 0, 400, 600, 5), record.Tokens);
    }

    [Fact]
    public void ClaudeFallsBackToTheSessionIdentityWithoutARequestId()
    {
        var line = ClaudeLine("""{"input_tokens":1,"output_tokens":1}""", requestId: null);
        Assert.True(ClaudeUsageLogParser.TryParse(line, out var record));
        Assert.Equal("claude:s1:msg_1", record.Key);
    }

    [Fact]
    public void ClaudeSkipsTheProxyMessageStartEstimate()
    {
        // Stop reason still null, input counted, nothing out and no cache fields: the
        // response has not happened yet.
        Assert.False(ClaudeUsageLogParser.TryParse(ClaudeLine("""{"input_tokens":1200,"output_tokens":0}""", stop: null), out _));
        // The same shape with cache fields present is a real (if output-less) response.
        Assert.True(ClaudeUsageLogParser.TryParse(
            ClaudeLine("""{"input_tokens":1200,"cache_read_input_tokens":0,"output_tokens":0}""", stop: null), out _));
    }

    [Theory]
    [InlineData("""{"type":"user","timestamp":"2026-09-02T06:59:42Z","message":{"usage":{"input_tokens":1}}}""")]
    [InlineData("""{"type":"assistant","timestamp":"2026-09-02T06:59:42Z","message":{"id":"m","model":"x","usage":{"input_tokens":0,"output_tokens":0}}}""")]
    [InlineData("""{"type":"assistant","message":{"id":"m","model":"x","usage":{"input_tokens":5,"output_tokens":5}}}""")]
    [InlineData("""{"type":"assistant","timestamp":"not a time","message":{"usage":{"input_tokens":5}}}""")]
    [InlineData("""{"type":"assistant","usage" truncated""")]
    [InlineData("")]
    public void ClaudeIgnoresLinesThatAreNotAPricedResponse(string line)
        => Assert.False(ClaudeUsageLogParser.TryParse(line, out _));

    [Fact]
    public void ClaudeKeepsTheModelNameAsWrittenWhenItIsNotAKnownId()
    {
        // "<synthetic>" and aliases are recorded under their own name and then priced as
        // unknown, never guessed at.
        Assert.True(ClaudeUsageLogParser.TryParse(ClaudeLine("""{"input_tokens":3,"output_tokens":4}""", model: "<synthetic>"), out var record));
        Assert.Equal("<synthetic>", record.Model);
    }

    // ── Codex ────────────────────────────────────────────────────────────────

    private const string Ts = "2026-09-15T03:58:09.207Z";

    private static string TurnContext(string model)
        => "{\"timestamp\":\"" + Ts + "\",\"type\":\"turn_context\",\"payload\":{\"turn_id\":\"t\",\"model\":\"" + model + "\",\"cwd\":\"C:\\\\x\"}}";

    private static string UsageRecord(string responseId, int input, int cached, int output)
        => "{\"timestamp\":\"" + Ts + "\",\"type\":\"token_usage_record\",\"payload\":{\"response_id\":\"" + responseId
           + "\",\"usage\":{\"input_tokens\":" + input + ",\"cached_input_tokens\":" + cached
           + ",\"cache_write_input_tokens\":0,\"output_tokens\":" + output + ",\"reasoning_output_tokens\":1}}}";

    private static string TokenCount(string? last, string? total)
        => "{\"timestamp\":\"" + Ts + "\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{"
           + string.Join(",", new[] { last is null ? null : "\"last_token_usage\":" + last, total is null ? null : "\"total_token_usage\":" + total }.Where(s => s is not null))
           + "}}}";

    private static string U(int input, int cached, int output)
        => "{\"input_tokens\":" + input + ",\"cached_input_tokens\":" + cached + ",\"output_tokens\":" + output + "}";

    [Fact]
    public void CodexUsageRecordTakesTheModelFromTheLatestTurnContext()
    {
        var parser = new CodexUsageLogParser();
        Assert.Null(parser.Feed(TurnContext("gpt-5.5")));
        var record = parser.Feed(UsageRecord("resp_1", 25886, 4864, 124));

        Assert.NotNull(record);
        Assert.Equal("gpt-5.5", record!.Model);
        // Codex counts cached tokens inside input; the record keeps the uncached share alone.
        Assert.Equal(new TokenTotals(25886 - 4864, 4864, 0, 0, 124), record.Tokens);
        Assert.Equal("codex:resp_1", record.Key);
    }

    [Fact]
    public void CodexUsesLastTokenUsageAndIgnoresAReemittedTotal()
    {
        var parser = new CodexUsageLogParser();
        parser.Feed(TurnContext("gpt-5.4"));
        var first = parser.Feed(TokenCount(U(100, 40, 10), U(100, 40, 10)));
        var repeat = parser.Feed(TokenCount(U(100, 40, 10), U(100, 40, 10)));
        var second = parser.Feed(TokenCount(U(50, 0, 5), U(150, 40, 15)));

        Assert.Equal(new TokenTotals(60, 40, 0, 0, 10), first!.Tokens);
        Assert.Null(repeat);
        Assert.Equal(new TokenTotals(50, 0, 0, 0, 5), second!.Tokens);
        Assert.Null(second.Key);
    }

    [Fact]
    public void CodexDerivesADeltaWhenOnlyTheRunningTotalIsPresent()
    {
        var parser = new CodexUsageLogParser();
        parser.Feed(TurnContext("gpt-5.4"));
        Assert.Equal(new TokenTotals(60, 40, 0, 0, 10), parser.Feed(TokenCount(null, U(100, 40, 10)))!.Tokens);
        Assert.Equal(new TokenTotals(30, 20, 0, 0, 5), parser.Feed(TokenCount(null, U(150, 60, 15)))!.Tokens);
        // A total below the watermark is a stale replay: it adds nothing and moves nothing.
        Assert.Null(parser.Feed(TokenCount(null, U(120, 50, 12))));
        Assert.Equal(new TokenTotals(10, 0, 0, 0, 1), parser.Feed(TokenCount(null, U(160, 60, 16)))!.Tokens);
    }

    [Fact]
    public void CodexIgnoresTokenCountsOnceAFileHasShownUsageRecords()
    {
        // New-format files write both; the per-response record is authoritative, so the
        // same response must not be counted again from its token_count.
        var parser = new CodexUsageLogParser();
        parser.Feed(TurnContext("gpt-5.5"));
        Assert.NotNull(parser.Feed(UsageRecord("resp_1", 100, 0, 10)));
        Assert.Null(parser.Feed(TokenCount(U(100, 0, 10), U(100, 0, 10))));
    }

    [Fact]
    public void CodexStateCarriesTheModelAndWatermarksAcrossReads()
    {
        var parser = new CodexUsageLogParser();
        parser.Feed(TurnContext("gpt-5.3-codex"));
        parser.Feed(TokenCount(null, U(100, 0, 10)));

        // A later incremental read resumes from the persisted state.
        var resumed = new CodexUsageLogParser(parser.Current);
        var record = resumed.Feed(TokenCount(null, U(130, 0, 13)));
        Assert.Equal("gpt-5.3-codex", record!.Model);
        Assert.Equal(new TokenTotals(30, 0, 0, 0, 3), record.Tokens);
    }

    [Fact]
    public void CodexWithoutATurnContextRecordsAnUnknownModel()
        => Assert.Equal("unknown", new CodexUsageLogParser().Feed(UsageRecord("r", 10, 0, 1))!.Model);

    [Theory]
    [InlineData("""{"timestamp":"2026-09-15T03:58:09Z","type":"response_item","payload":{"type":"message","content":"we discussed token_count here"}}""")]
    [InlineData("""{"type":"session_meta","payload":{"id":"x"}}""")]
    [InlineData("""{"timestamp":"2026-09-15T03:58:09Z","type":"event_msg","payload":{"type":"token_count","info":null}}""")]
    [InlineData("not json but mentions token_count")]
    public void CodexIgnoresLinesThatCarryNoUsage(string line)
        => Assert.Null(new CodexUsageLogParser().Feed(line));
}
