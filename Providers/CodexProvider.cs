using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Gauge.Localization;
using Gauge.Models;
using Gauge.Providers.Internal;
using Gauge.Services;
using System.Net;

namespace Gauge.Providers;

/// <summary>
/// Reads Codex usage from the ChatGPT backend usage endpoint
/// (<c>GET https://chatgpt.com/backend-api/wham/usage</c>) using the OAuth token the
/// Codex CLI stores in <c>~/.codex/auth.json</c>. This returns the real rate-limit
/// utilization and reset times plus the plan tier. Window roles are derived from each
/// window's <c>limit_window_seconds</c>, not from its primary/secondary position: during
/// plan simplification the weekly window can be returned as primary with no secondary.
///
/// A missing credential is a clean empty-data result. Network and API failures
/// propagate so the coordinator keeps showing its last good snapshot rather than
/// replacing it with an empty success.
///
/// EXPIRED TOKEN: the Codex access token is a ChatGPT-issued JWT that lives ~10 days, so
/// after a long idle it is already expired at boot. When an <see cref="IDelegatedTokenRefresher"/>
/// is supplied, an expired/rejected token triggers a delegated refresh (the CLI refreshes
/// its own token via <c>codex doctor</c>) and a re-read, so usage works without first
/// opening Codex.
/// </summary>
public sealed class CodexProvider : IUsageProvider
{
    private const string UsageUrl = "https://chatgpt.com/backend-api/wham/usage";

    private readonly HttpClient _http;
    private readonly ICredentialSource _credentials;
    private readonly IDelegatedTokenRefresher? _refresher;

    public CodexProvider(HttpClient http, ICredentialSource credentials, IDelegatedTokenRefresher? refresher = null)
    {
        _http = http;
        _credentials = credentials;
        _refresher = refresher;
    }

    public ToolKind Tool => ToolKind.Codex;
    public string ToolName => ToolCatalog.For(ToolKind.Codex).DisplayName;

    public async Task<UsageSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        var credentialResult = await _credentials.ReadAsync(ToolKind.Codex, cancellationToken);

        // The Codex access token is a ~10-day ChatGPT JWT, so after a long idle it is
        // already expired at boot (reported Invalid by the credential source). Rather than
        // fail until Codex is opened, ask the CLI to refresh its own token (it owns the
        // refresh-token rotation, so this can't break its login), then re-read. Done before
        // the no-token check below so a successful refresh is picked up.
        if (credentialResult.Status == CredentialReadStatus.Invalid && _refresher is not null
            && await _refresher.TryRefreshAsync(cancellationToken))
        {
            credentialResult = await _credentials.ReadAsync(ToolKind.Codex, cancellationToken);
        }

        var credentials = credentialResult.Credential;

        if (credentialResult.Status == CredentialReadStatus.Invalid)
        {
            throw new AuthenticationRequiredException(ToolKind.Codex, HttpStatusCode.Unauthorized);
        }

        // No token (not logged in): a legitimate "no data yet" state, not a failure.
        if (credentials?.AccessToken is not { Length: > 0 } token)
        {
            return new UsageSnapshot
            {
                ToolName = ToolName,
                Plan = null,
                Windows = Array.Empty<UsageWindow>(),
                CapturedAt = DateTimeOffset.Now,
            };
        }

        // wham/usage is the same endpoint the Codex CLI itself polls every 60s, so our
        // 60s cadence needs no extra throttling. Let fetch failures (network/429)
        // propagate rather than swallowing them into an empty success — that way the
        // coordinator keeps the last good snapshot instead of clearing the card.
        try
        {
            return BuildSnapshot(await FetchUsageAsync(token, credentials.AccountId, cancellationToken));
        }
        catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            // The server rejected a token that looked valid locally (e.g. revoked, or it
            // expired between read and call). Try one delegated refresh and, if the CLI
            // hands us a fresh token, retry the fetch once before giving up.
            if (_refresher is not null && await _refresher.TryRefreshAsync(cancellationToken))
            {
                var refreshed = await _credentials.ReadAsync(ToolKind.Codex, cancellationToken);
                if (refreshed.Status == CredentialReadStatus.Available
                    && refreshed.Credential?.AccessToken is { Length: > 0 } freshToken)
                {
                    try
                    {
                        return BuildSnapshot(await FetchUsageAsync(freshToken, refreshed.Credential.AccountId, cancellationToken));
                    }
                    catch (HttpRequestException retryEx) when (retryEx.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                    {
                        throw new AuthenticationRequiredException(ToolKind.Codex, retryEx.StatusCode!.Value);
                    }
                }
            }
            throw new AuthenticationRequiredException(ToolKind.Codex, ex.StatusCode!.Value);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Debug.WriteLine($"[Gauge] CodexProvider usage fetch failed: {ex.Message}");
            throw;
        }
    }

    private UsageSnapshot BuildSnapshot((string? Plan, List<UsageWindow> Windows) result) => new()
    {
        ToolName = ToolName,
        Plan = result.Plan,
        Windows = result.Windows,
        CapturedAt = DateTimeOffset.Now,
    };

    private async Task<(string? Plan, List<UsageWindow> Windows)> FetchUsageAsync(
        string token, string? accountId, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, UsageUrl);
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");
        request.Headers.TryAddWithoutValidation("User-Agent", "Gauge/1.0");
        if (!string.IsNullOrEmpty(accountId))
        {
            request.Headers.TryAddWithoutValidation("ChatGPT-Account-Id", accountId);
        }

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, default, cancellationToken);
        var root = document.RootElement;

        var plan = MapPlan(root.GetStringOrNull("plan_type"));

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

        return (plan, windows);
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
        var type = ClassifyWindow(durationSeconds) ?? fallbackType;
        TimeSpan? duration = durationSeconds is > 0 and <= 31_622_400
            ? TimeSpan.FromSeconds(durationSeconds.Value)
            : type switch
            {
                UsageWindowType.FiveHour => TimeSpan.FromHours(5),
                UsageWindowType.Weekly => TimeSpan.FromDays(7),
                _ => null,
            };
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

    private static UsageWindowType? ClassifyWindow(long? durationSeconds) => durationSeconds switch
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
            var name = NormalizeText(entry.GetStringOrNull("limit_name"));
            var feature = NormalizeText(entry.GetStringOrNull("metered_feature"));
            var displayName = name ?? feature;
            var identity = feature ?? name;
            if (displayName is null || identity is null
                || entry.GetObjectOrNull("rate_limit") is not { } rateLimit)
            {
                continue;
            }

            var slug = Slug(identity);
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

    private static string? NormalizeText(string? value)
        => value?.Trim() is { Length: > 0 } text ? text : null;

    private static string Slug(string value)
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

    private static string? MapPlan(string? planType) => planType?.ToLowerInvariant() switch
    {
        null or "" => null,
        "plus" => "Plus",
        "pro" => "Pro",
        "free" => "Free",
        "go" => "Go",
        "business" => "Business",
        "team" => "Team",
        "enterprise" => "Enterprise",
        var other => char.ToUpperInvariant(other[0]) + other[1..],
    };
}
