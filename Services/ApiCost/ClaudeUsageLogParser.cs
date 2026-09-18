using System.Globalization;
using System.Text.Json;
using Gauge.Models;

namespace Gauge.Services.ApiCost;

/// <summary>
/// Reads one assistant line of a Claude Code session log
/// (<c>~/.claude/projects/**/*.jsonl</c>) into a <see cref="UsageLogRecord"/>. Only the
/// token counts, model name, timestamp and message identity are read; the conversation
/// content on the same line is never touched.
///
/// Claude Code writes a streamed message more than once (each write repeats the same
/// usage), so the record's key — the message id with the request id, or with the session
/// id when a request id is absent — lets the caller keep one. A line whose stop reason is
/// still null with input but no output and no cache fields is the proxy's message-start
/// estimate, not a completed response, and is skipped.
/// </summary>
public static class ClaudeUsageLogParser
{
    private const string UnknownModel = "unknown";

    /// <summary>Cheap byte-level check so the JSON parser only runs on candidate lines.</summary>
    public static bool MayContainUsage(string line)
        => line.Contains("\"type\":\"assistant\"", StringComparison.Ordinal) && line.Contains("\"usage\"", StringComparison.Ordinal);

    public static bool TryParse(string line, out UsageLogRecord record)
    {
        record = null!;
        if (!MayContainUsage(line)) return false;
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;
            if (!root.TryGetProperty("type", out var type) || type.GetString() != "assistant") return false;
            if (!root.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object) return false;
            if (!message.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object) return false;
            if (!root.TryGetProperty("timestamp", out var timestampElement)
                || timestampElement.GetString() is not { } timestampText
                || !DateTimeOffset.TryParse(timestampText, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var timestamp))
                return false;

            var input = Long(usage, "input_tokens");
            var cacheWrite = Long(usage, "cache_creation_input_tokens");
            var cacheRead = Long(usage, "cache_read_input_tokens");
            var output = Long(usage, "output_tokens");
            long cacheWrite1h = 0;
            if (usage.TryGetProperty("cache_creation", out var creation) && creation.ValueKind == JsonValueKind.Object)
            {
                cacheWrite1h = Long(creation, "ephemeral_1h_input_tokens");
                // The five-minute and one-hour slices partition the cache-write count.
                cacheWrite = Math.Max(0, cacheWrite - cacheWrite1h);
            }

            var incomplete = message.TryGetProperty("stop_reason", out var stop) && stop.ValueKind == JsonValueKind.Null
                && input > 0 && output == 0
                && !usage.TryGetProperty("cache_read_input_tokens", out _)
                && !usage.TryGetProperty("cache_creation_input_tokens", out _);
            if (incomplete) return false;

            var tokens = new TokenTotals(input, cacheRead, cacheWrite, cacheWrite1h, output);
            if (tokens.IsZero) return false;

            var model = message.TryGetProperty("model", out var modelElement) && modelElement.GetString() is { Length: > 0 } name
                ? name : UnknownModel;
            record = new UsageLogRecord(timestamp, model, tokens, Key(root, message));
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string? Key(JsonElement root, JsonElement message)
    {
        var messageId = String(message, "id");
        if (string.IsNullOrEmpty(messageId)) return null;
        var requestId = String(root, "requestId");
        if (!string.IsNullOrEmpty(requestId)) return $"claude:{messageId}:{requestId}";
        var sessionId = String(root, "sessionId") ?? String(root, "session_id");
        return string.IsNullOrEmpty(sessionId) ? null : $"claude:{sessionId}:{messageId}";
    }

    private static string? String(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static long Long(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number)
            ? Math.Max(0, number) : 0;
}
