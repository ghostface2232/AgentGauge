using System.Net.Http;
using System.Text.Json;
using Gauge.Localization;
using Gauge.Models;
using Gauge.Providers.Internal;
using Gauge.Services;

namespace Gauge.Providers;

/// <summary>
/// Reads Codex usage from the ChatGPT backend usage endpoint
/// (<c>GET https://chatgpt.com/backend-api/wham/usage</c>) using the OAuth token the
/// Codex CLI stores in <c>~/.codex/auth.json</c>. This returns the real rate-limit
/// utilization and reset times plus the plan tier. Window roles are derived from each
/// window's <c>limit_window_seconds</c>, not from its primary/secondary position: during
/// plan simplification the weekly window can be returned as primary with no secondary.
///
/// THROTTLING: wham/usage is the same endpoint the Codex CLI itself polls every 60s, so
/// the happy path needs no per-provider cap (unlike Claude) — every scheduler cycle and
/// popover-open refresh fetches live. But that same unconditional fetching means a
/// policy tightening on OpenAI's side would hit on every popover open, so retryable
/// statuses (429/5xx) arm the shared cooldown: its Retry-After is honored when sent,
/// background fetches serve the cached snapshot until it lapses, and a user-initiated
/// refresh still always goes out. Other failures (network, parse) propagate so the
/// coordinator keeps its last good snapshot and marks the card stale; only a missing
/// token is a clean "no data" state.
///
/// EXPIRED TOKEN: the Codex access token is a ChatGPT-issued JWT that lives ~10 days, so
/// after a long idle it is already expired at boot. When an <see cref="IDelegatedTokenRefresher"/>
/// is supplied, an expired/rejected token triggers a delegated refresh (the CLI refreshes
/// its own token via <c>codex doctor</c>) and a re-read, so usage works without first
/// opening Codex.
/// </summary>
public sealed class CodexProvider : UsageProviderBase
{
    private const string UsageUrl = "https://chatgpt.com/backend-api/wham/usage";

    private static readonly UsageFetchPolicy Policy = new()
    {
        // Same escalation as Claude when the server sends no Retry-After: 2, 4, 8, 16,
        // 30(cap) minutes. The first step sits under the 3-minute periodic cadence, so
        // an isolated 429 costs at most one popover-open freshness window.
        RetryBackoff = new BackoffPolicy(TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(30)),
    };

    private static readonly IReadOnlyDictionary<string, string> KnownPlans = new Dictionary<string, string>
    {
        ["plus"] = "Plus",
        ["pro"] = "Pro",
        ["free"] = "Free",
        ["go"] = "Go",
        ["business"] = "Business",
        ["team"] = "Team",
        ["enterprise"] = "Enterprise",
    };

    private readonly HttpClient _http;

    public CodexProvider(
        HttpClient http,
        ICredentialSource credentials,
        IDelegatedTokenRefresher? refresher = null,
        TimeProvider? time = null)
        : base(credentials, Policy, refresher, time)
    {
        _http = http;
    }

    public override ToolKind Tool => ToolKind.Codex;

    protected override async Task<UsageSnapshot> FetchSnapshotAsync(
        ToolCredential credential, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, UsageUrl);
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {credential.AccessToken}");
        request.Headers.TryAddWithoutValidation("User-Agent", "Gauge/1.0");
        if (!string.IsNullOrEmpty(credential.AccountId))
        {
            request.Headers.TryAddWithoutValidation("ChatGPT-Account-Id", credential.AccountId);
        }

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        EnsureUsageSuccess(response);

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, default, cancellationToken);
        var root = document.RootElement;

        var plan = ProviderText.PlanLabel(root.GetStringOrNull("plan_type"), KnownPlans);

        var windows = new List<UsageWindow>();
        if (root.GetObjectOrNull("rate_limit") is { } rateLimit)
        {
            if (ParseWindow(rateLimit, "primary_window", UsageWindowType.FiveHour) is { } primary)
            {
                windows.Add(primary);
            }
            if (ParseWindow(rateLimit, "secondary_window", UsageWindowType.Weekly) is { } secondary)
            {
                windows.Add(secondary);
            }
        }
        windows.AddRange(ParseAdditionalRateLimits(root));

        return Snapshot(plan, windows);
    }

    /// <summary>
    /// Parses one rate-limit window: <c>{ "used_percent": 0–100, "reset_at": epochSeconds }</c>.
    /// </summary>
    private static UsageWindow? ParseWindow(
        JsonElement rateLimit,
        string property,
        UsageWindowType fallbackType,
        string? idPrefix = null,
        string? groupLabel = null)
    {
        if (rateLimit.GetObjectOrNull(property) is not { } window
            || window.GetDoubleOrNull("used_percent") is not { } usedPercent)
        {
            return null;
        }

        var resetTime = window.GetInt64OrNull("reset_at") is { } epoch
            ? DateTimeOffset.FromUnixTimeSeconds(epoch)
            : (DateTimeOffset?)null;
        var durationSeconds = window.GetInt64OrNull("limit_window_seconds");
        UsageWindowType type;
        TimeSpan? duration;
        if (window.TryGetProperty("limit_window_seconds", out _))
        {
            // Once the provider supplies a duration, it is the contract. Do not silently
            // reinterpret a new or malformed duration by primary/secondary position.
            if (durationSeconds is not { } seconds
                || ClassifyWindow(seconds) is not { } classifiedType)
            {
                return null;
            }

            type = classifiedType;
            duration = TimeSpan.FromSeconds(seconds);
        }
        else
        {
            // Older responses omitted the duration and used positional semantics. Retain
            // that compatibility without inventing a duration for the UI's pace hint.
            type = fallbackType;
            duration = null;
        }
        var id = idPrefix is null
            ? null
            : $"{idPrefix}-{(durationSeconds?.ToString() ?? type.ToString().ToLowerInvariant())}";

        return new UsageWindow
        {
            Id = id,
            Type = type,
            GroupLabel = groupLabel,
            UsedRatio = Math.Clamp(usedPercent / 100.0, 0.0, 1.0),
            Label = WindowLabels.For(type),
            ResetTime = resetTime,
            Duration = duration,
        };
    }

    private static UsageWindowType? ClassifyWindow(long durationSeconds) => durationSeconds switch
    {
        5 * 60 * 60 => UsageWindowType.FiveHour,
        7 * 24 * 60 * 60 => UsageWindowType.Weekly,
        _ => null,
    };

    /// <summary>
    /// New Codex plans may expose named model/feature limits alongside the account-wide
    /// windows. They are optional and parsed lossily so an empty or malformed entry never
    /// affects the primary usage display.
    /// </summary>
    private static IReadOnlyList<UsageWindow> ParseAdditionalRateLimits(JsonElement root)
    {
        if (!root.TryGetArray("additional_rate_limits", out var additional))
        {
            return Array.Empty<UsageWindow>();
        }

        var windows = new List<UsageWindow>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in additional.EnumerateArray())
        {
            var name = ProviderText.Normalize(entry.GetStringOrNull("limit_name"));
            var feature = ProviderText.Normalize(entry.GetStringOrNull("metered_feature"));
            var displayName = AdditionalLimitDisplayName(name ?? feature);
            var identity = feature ?? name;
            if (displayName is null || identity is null
                || entry.GetObjectOrNull("rate_limit") is not { } rateLimit)
            {
                continue;
            }

            var slug = ProviderText.Slug(identity);
            if (slug.Length == 0)
            {
                continue;
            }
            var prefix = $"codex-additional-{slug}";

            foreach (var (property, fallbackType) in new[]
                     {
                         ("primary_window", UsageWindowType.FiveHour),
                         ("secondary_window", UsageWindowType.Weekly),
                     })
            {
                if (ParseWindow(rateLimit, property, fallbackType, prefix, displayName) is { } window
                    && seen.Add(window.Key))
                {
                    windows.Add(window);
                }
            }
        }

        return windows;
    }

    private static string? AdditionalLimitDisplayName(string? raw)
        => raw is not null && ProviderText.Slug(raw) == "gpt-reserve"
            ? "Luna Reserve"
            : raw;
}
