using System.Globalization;
using System.Text.Json;
using Gauge.Models;

namespace Gauge.Services.ApiCost;

/// <summary>
/// Reads a Codex rollout log (<c>~/.codex/sessions/**/*.jsonl</c>) line by line into
/// <see cref="UsageLogRecord"/>s. Stateful per file, because the events do not stand alone:
/// the model comes from the latest <c>turn_context</c>, and the older <c>token_count</c>
/// events carry cumulative totals whose increase is the response's consumption.
///
/// Two generations of log coexist. Recent files write a <c>token_usage_record</c> per
/// response with that response's own usage — the authoritative source, keyed by response
/// id. Older files only have <c>event_msg</c>/<c>token_count</c>, where
/// <c>last_token_usage</c> is the response and <c>total_token_usage</c> the running sum;
/// once a file has shown a usage record its token counts are ignored so the same response
/// is never counted from both. Only the token fields, model and timestamp are read; the
/// conversation content in the same file is never touched.
/// </summary>
public sealed class CodexUsageLogParser
{
    private const string UnknownModel = "unknown";

    /// <summary>The per-file state that must survive between incremental reads.</summary>
    public sealed class State
    {
        public string? Model { get; set; }
        public bool HasUsageRecords { get; set; }
        public long TotalInput { get; set; }
        public long TotalCached { get; set; }
        public long TotalCacheWrite { get; set; }
        public long TotalOutput { get; set; }
    }

    public State Current { get; }

    public CodexUsageLogParser(State? state = null) => Current = state ?? new State();

    /// <summary>Cheap byte-level check so the JSON parser only runs on candidate lines.</summary>
    public static bool MayMatter(string line)
        => line.Contains("\"turn_context\"", StringComparison.Ordinal)
        || line.Contains("\"token_usage_record\"", StringComparison.Ordinal)
        || line.Contains("\"token_count\"", StringComparison.Ordinal);

    /// <summary>Feeds one line; returns a record when the line completes a response's usage.</summary>
    public UsageLogRecord? Feed(string line)
    {
        if (!MayMatter(line)) return null;
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            var type = String(root, "type");
            if (!root.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object) return null;

            switch (type)
            {
                case "turn_context":
                    // An explicit empty model clears stale context; an absent one keeps it.
                    if (payload.TryGetProperty("model", out var model) && model.ValueKind == JsonValueKind.String)
                        Current.Model = model.GetString() is { Length: > 0 } name ? name : null;
                    return null;
                case "token_usage_record":
                    Current.HasUsageRecords = true;
                    if (!payload.TryGetProperty("usage", out var usage) || !Timestamp(root, out var at)) return null;
                    var tokens = Tokens(usage);
                    if (tokens.IsZero) return null;
                    var id = String(payload, "response_id") ?? String(payload, "turn_id");
                    return new UsageLogRecord(at, Current.Model ?? UnknownModel, tokens, id is null ? null : $"codex:{id}");
                case "event_msg":
                    return String(payload, "type") == "token_count" ? TokenCount(root, payload) : null;
                default:
                    return null;
            }
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private UsageLogRecord? TokenCount(JsonElement root, JsonElement payload)
    {
        if (Current.HasUsageRecords) return null;
        if (!payload.TryGetProperty("info", out var info) || info.ValueKind != JsonValueKind.Object) return null;
        if (!Timestamp(root, out var at)) return null;

        var hasTotal = info.TryGetProperty("total_token_usage", out var total) && total.ValueKind == JsonValueKind.Object;
        var (input, cached, cacheWrite, output) = hasTotal ? Raw(total) : (0L, 0L, 0L, 0L);
        // A re-emitted, unchanged total is the same response again, not new usage.
        if (hasTotal && input == Current.TotalInput && cached == Current.TotalCached
            && cacheWrite == Current.TotalCacheWrite && output == Current.TotalOutput)
            return null;

        TokenTotals tokens;
        if (info.TryGetProperty("last_token_usage", out var last) && last.ValueKind == JsonValueKind.Object)
        {
            tokens = Tokens(last);
        }
        else if (hasTotal)
        {
            tokens = Normalize(
                Math.Max(0, input - Current.TotalInput), Math.Max(0, cached - Current.TotalCached),
                Math.Max(0, cacheWrite - Current.TotalCacheWrite), Math.Max(0, output - Current.TotalOutput));
        }
        else
        {
            return null;
        }

        if (hasTotal)
        {
            // Watermarks never move backwards: a snapshot below them is a stale replay.
            Current.TotalInput = Math.Max(Current.TotalInput, input);
            Current.TotalCached = Math.Max(Current.TotalCached, cached);
            Current.TotalCacheWrite = Math.Max(Current.TotalCacheWrite, cacheWrite);
            Current.TotalOutput = Math.Max(Current.TotalOutput, output);
        }
        return tokens.IsZero ? null : new UsageLogRecord(at, Current.Model ?? UnknownModel, tokens, null);
    }

    // Codex counts the cached and newly cached shares inside input_tokens; the record keeps
    // the uncached share alone in Input so every provider means the same thing by it.
    private static TokenTotals Tokens(JsonElement usage)
    {
        var (input, cached, cacheWrite, output) = Raw(usage);
        return Normalize(input, cached, cacheWrite, output);
    }

    private static TokenTotals Normalize(long input, long cached, long cacheWrite, long output)
        => new(Math.Max(0, input - cached - cacheWrite), cached, cacheWrite, 0, output);

    private static (long Input, long Cached, long CacheWrite, long Output) Raw(JsonElement usage) => (
        Long(usage, "input_tokens"),
        Math.Max(Long(usage, "cached_input_tokens"), Long(usage, "cache_read_input_tokens")),
        Long(usage, "cache_write_input_tokens"),
        Long(usage, "output_tokens"));

    private static bool Timestamp(JsonElement root, out DateTimeOffset at)
    {
        at = default;
        return String(root, "timestamp") is { } text
            && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out at);
    }

    private static string? String(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static long Long(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number)
            ? Math.Max(0, number) : 0;
}
