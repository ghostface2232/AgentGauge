using System.Text.Json;

namespace Gauge.Providers;

/// <summary>
/// A 200 response that does not look like this endpoint's usage document. These endpoints are
/// undocumented, so a change of shape parses to no windows at all, and returning that as a
/// success would overwrite the last good snapshot and the disk cache with nothing. A window
/// that is merely null or absent is still a legitimate empty result.
/// </summary>
public sealed class UsageResponseShapeException(string toolName)
    : Exception($"{toolName} usage response has an unrecognized shape.")
{
    public static void ThrowUnlessAnyProperty(JsonElement root, string toolName, params string[] properties)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !properties.Any(property => root.TryGetProperty(property, out _)))
        {
            throw new UsageResponseShapeException(toolName);
        }
    }
}
