using System.Text.Json;
using Gauge.Localization;
using Gauge.Models;

namespace Gauge.Providers.Internal;

/// <summary>
/// Tolerant parser for Antigravity's <c>RetrieveUserQuotaSummary</c> response. The language
/// server reports quota as groups (model families) of buckets (one per window); this flattens
/// them into up to four <see cref="UsageWindow"/>s — a 5-hour and a weekly limit per family —
/// using each bucket's stable <c>bucketId</c> as the window <see cref="UsageWindow.Id"/> and its
/// <c>window</c> field ("5h"/"weekly") as the <see cref="UsageWindowType"/>.
///
/// This is an unstable internal API, so every field is optional: a bucket missing a stable id,
/// a recognized window, or a usable remaining fraction is skipped rather than guessed at, and a
/// bucket is never assumed fully spent just because its usage is absent. Only unparseable JSON
/// surfaces as an exception — the caller treats that as a provider failure that keeps the last
/// good snapshot.
/// </summary>
internal static class AntigravityQuotaParser
{
    public static IReadOnlyList<UsageWindow> Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return Parse(document.RootElement);
    }

    public static IReadOnlyList<UsageWindow> Parse(JsonElement root)
    {
        // The payload arrives under "response"; tolerate a "summary" alias and a bare root that
        // already carries "groups", mirroring the kind of drift seen across binary versions.
        var payload = root.GetObjectOrNull("response")
            ?? root.GetObjectOrNull("summary")
            ?? root;

        if (!payload.TryGetArray("groups", out var groups))
        {
            return Array.Empty<UsageWindow>();
        }

        var windows = new List<UsageWindow>();
        foreach (var group in groups.EnumerateArray())
        {
            if (!group.TryGetArray("buckets", out var buckets))
            {
                continue;
            }

            foreach (var bucket in buckets.EnumerateArray())
            {
                if (ParseBucket(bucket) is { } window)
                {
                    windows.Add(window);
                }
            }
        }

        return windows;
    }

    private static UsageWindow? ParseBucket(JsonElement bucket)
    {
        if (bucket.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        // bucketId is the provider-stable identity; without it the window can't be reconciled
        // across refreshes or kept distinct from its sibling family, so the bucket is unusable.
        if (bucket.GetStringOrNull("bucketId") is not { } rawId || rawId.Trim() is not { Length: > 0 } bucketId)
        {
            return null;
        }

        // A disabled bucket carries no live limit — omit it rather than render it as 0% used.
        if (bucket.TryGetProperty("disabled", out var disabled) && disabled.ValueKind == JsonValueKind.True)
        {
            return null;
        }

        if (MapWindowType(bucket.GetStringOrNull("window")) is not { } type)
        {
            return null;
        }

        // No usable fraction → skip. Never assume a window is fully spent from a missing value.
        if (ResolveRemainingFraction(bucket) is not { } remaining)
        {
            return null;
        }

        return new UsageWindow
        {
            Id = bucketId,
            Type = type,
            GroupLabel = FamilyLabel(bucketId),
            UsedRatio = Math.Clamp(1.0 - remaining, 0.0, 1.0),
            Label = WindowLabels.For(type),
            ResetTime = ParseResetTime(bucket),
        };
    }

    // The model family a bucket belongs to, from its stable id prefix (gemini-*, 3p-*). Used as
    // the card's group heading; null for an unrecognized prefix (rendered without a group).
    private static string? FamilyLabel(string bucketId)
        => bucketId.StartsWith("gemini", StringComparison.OrdinalIgnoreCase) ? "Gemini"
            : bucketId.StartsWith("3p", StringComparison.OrdinalIgnoreCase) ? "Claude/GPT"
            : null;

    private static UsageWindowType? MapWindowType(string? window) => window switch
    {
        "5h" => UsageWindowType.FiveHour,
        "weekly" => UsageWindowType.Weekly,
        _ => null,
    };

    // remainingFraction is a direct float in the observed build, but the field is a protobuf
    // oneof, so the wrapped forms ({remainingFraction} / {case,value}) are tolerated too.
    private static double? ResolveRemainingFraction(JsonElement bucket)
    {
        if (bucket.GetDoubleOrNull("remainingFraction") is { } direct)
        {
            return direct;
        }

        if (bucket.GetObjectOrNull("remaining") is { } remaining)
        {
            if (remaining.GetDoubleOrNull("remainingFraction") is { } nested)
            {
                return nested;
            }
            if (remaining.GetStringOrNull("case") == "remainingFraction")
            {
                return remaining.GetDoubleOrNull("value");
            }
        }

        return null;
    }

    // ISO-8601 (the observed form) is tried first; a bare number is tolerated as epoch seconds.
    private static DateTimeOffset? ParseResetTime(JsonElement bucket)
        => bucket.GetDateTimeOffsetOrNull("resetTime")
            ?? (bucket.GetInt64OrNull("resetTime") is { } epoch
                ? DateTimeOffset.FromUnixTimeSeconds(epoch)
                : null);
}
