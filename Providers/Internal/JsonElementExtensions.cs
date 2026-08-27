using System.Globalization;
using System.Text.Json;

namespace Gauge.Providers.Internal;

/// <summary>
/// Defensive accessors over <see cref="JsonElement"/>. A provider API's schema can
/// vary, so every lookup tolerates missing or mistyped fields and returns a default
/// instead of throwing.
/// </summary>
internal static class JsonElementExtensions
{
    public static long GetLongOrDefault(this JsonElement element, string property)
        => element.ValueKind == JsonValueKind.Object
           && element.TryGetProperty(property, out var value)
           && value.ValueKind == JsonValueKind.Number
           && value.TryGetInt64(out var result)
            ? result
            : 0L;

    public static long? GetInt64OrNull(this JsonElement element, string property)
        => element.ValueKind == JsonValueKind.Object
           && element.TryGetProperty(property, out var value)
           && value.ValueKind == JsonValueKind.Number
           && value.TryGetInt64(out var result)
            ? result
            : null;

    public static double? GetDoubleOrNull(this JsonElement element, string property)
        => element.ValueKind == JsonValueKind.Object
           && element.TryGetProperty(property, out var value)
           && value.ValueKind == JsonValueKind.Number
           && value.TryGetDouble(out var result)
            ? result
            : null;

    public static bool? GetBoolOrNull(this JsonElement element, string property)
        => element.ValueKind == JsonValueKind.Object
           && element.TryGetProperty(property, out var value)
           && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : null;

    public static JsonElement? GetObjectOrNull(this JsonElement element, string property)
        => element.ValueKind == JsonValueKind.Object
           && element.TryGetProperty(property, out var value)
           && value.ValueKind == JsonValueKind.Object
            ? value
            : null;

    public static string? GetStringOrNull(this JsonElement element, string property)
        => element.ValueKind == JsonValueKind.Object
           && element.TryGetProperty(property, out var value)
           && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>
    /// An API timestamp, parsed with InvariantCulture (never the UI language's culture) and
    /// normalized to UTC. A value carrying an offset keeps its instant; one carrying none —
    /// a bare date such as <c>"2026-09-01"</c>, or a naked datetime — is read as UTC.
    ///
    /// The framework default would instead apply the reader's own offset, which for a server
    /// timestamp is never right: in UTC+9 it would move a reset nine hours earlier and shift
    /// the notification evaluator's reset-advance comparison with it. No provider here
    /// reports wall-clock time in the reader's zone, so UTC is the only sound reading of an
    /// omitted offset. These endpoints are undocumented (GitHub's <c>quota_reset_date_utc</c>
    /// most of all), so the parse is written to be correct under either shape rather than
    /// against the one a captured response happened to carry.
    /// </summary>
    public static DateTimeOffset? GetDateTimeOffsetOrNull(this JsonElement element, string property)
        => element.GetStringOrNull(property) is { } text
           && DateTimeOffset.TryParse(
               text,
               CultureInfo.InvariantCulture,
               DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
               out var result)
            ? result
            : null;

    public static bool TryGetArray(this JsonElement element, string property, out JsonElement array)
    {
        if (element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(property, out array)
            && array.ValueKind == JsonValueKind.Array)
        {
            return true;
        }

        array = default;
        return false;
    }
}
