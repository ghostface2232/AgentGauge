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

    public static DateTimeOffset? GetDateTimeOffsetOrNull(this JsonElement element, string property)
        => element.GetStringOrNull(property) is { } text
           && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var result)
            ? result
            : null;

    public static DateOnly? GetDateOnlyOrNull(this JsonElement element, string property)
        => element.GetStringOrNull(property) is { } text
           && DateOnly.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var result)
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
