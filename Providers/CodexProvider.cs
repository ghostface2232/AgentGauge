using System.Diagnostics;
using System.Net.Http;
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
/// Codex CLI stores in <c>~/.codex/auth.json</c>. This returns the real 5-hour
/// (primary) and weekly (secondary) rate-limit utilization and reset times, plus the
/// plan tier — the same data the CLI itself sees, and always current (unlike scanning
/// local session logs, which go stale once Codex hasn't run for a while).
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
            if (ParseWindow(rateLimit, "primary_window", UsageWindowType.FiveHour) is { } fiveHour)
            {
                windows.Add(fiveHour);
            }
            if (ParseWindow(rateLimit, "secondary_window", UsageWindowType.Weekly) is { } weekly)
            {
                windows.Add(weekly);
            }
        }

        return (plan, windows);
    }

    /// <summary>
    /// Parses one rate-limit window: <c>{ "used_percent": 0–100, "reset_at": epochSeconds }</c>.
    /// </summary>
    private static UsageWindow? ParseWindow(JsonElement rateLimit, string property, UsageWindowType type)
    {
        if (rateLimit.GetObjectOrNull(property) is not { } window
            || window.GetDoubleOrNull("used_percent") is not { } usedPercent)
        {
            return null;
        }

        var resetTime = window.GetInt64OrNull("reset_at") is { } epoch
            ? DateTimeOffset.FromUnixTimeSeconds(epoch)
            : (DateTimeOffset?)null;

        return new UsageWindow
        {
            Type = type,
            UsedRatio = Math.Clamp(usedPercent / 100.0, 0.0, 1.0),
            Label = WindowLabels.For(type),
            ResetTime = resetTime,
        };
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
