using System.Text;

namespace Gauge.Providers.Internal;

/// <summary>
/// Small text helpers shared by the HTTP usage providers, so each provider file keeps
/// only its endpoint's parsing rules.
/// </summary>
internal static class ProviderText
{
    /// <summary>Trimmed non-empty text, or null — treats whitespace-only as absent.</summary>
    public static string? Normalize(string? value)
        => value?.Trim() is { Length: > 0 } text ? text : null;

    /// <summary>
    /// Lowercase letters/digits with single dashes between runs (e.g. "GPT-5 Codex" →
    /// "gpt-5-codex"). Used to derive stable window ids from provider-reported names.
    /// </summary>
    public static string Slug(string value)
    {
        var builder = new StringBuilder(value.Length);
        var lastWasDash = false;
        foreach (var character in value.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(character);
                lastWasDash = false;
            }
            else if (!lastWasDash && builder.Length > 0)
            {
                builder.Append('-');
                lastWasDash = true;
            }
        }
        return builder.ToString().TrimEnd('-');
    }

    /// <summary>
    /// Maps a raw API plan/tier value to its display label: known values (lowercase keys)
    /// map explicitly, an unknown non-empty value degrades to itself capitalized rather
    /// than being hidden, and null/empty stays null.
    /// </summary>
    public static string? PlanLabel(string? raw, IReadOnlyDictionary<string, string> known)
    {
        var normalized = raw?.ToLowerInvariant();
        if (string.IsNullOrEmpty(normalized))
        {
            return null;
        }
        return known.TryGetValue(normalized, out var label)
            ? label
            : char.ToUpperInvariant(normalized[0]) + normalized[1..];
    }
}
