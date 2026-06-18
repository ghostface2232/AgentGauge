using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using Gauge.Models;
using Gauge.Providers.Internal;

namespace Gauge.Providers;

/// <summary>
/// Reads Codex usage from the ChatGPT backend usage endpoint
/// (<c>GET https://chatgpt.com/backend-api/wham/usage</c>) using the OAuth token the
/// Codex CLI stores in <c>~/.codex/auth.json</c>. This returns the real 5-hour
/// (primary) and weekly (secondary) rate-limit utilization and reset times, plus the
/// plan tier — the same data the CLI itself sees, and always current (unlike scanning
/// local session logs, which go stale once Codex hasn't run for a while).
///
/// Degrades gracefully: a missing credential or network error yields an empty window
/// list, and the coordinator keeps showing the last good snapshot.
/// </summary>
public sealed class CodexProvider : IUsageProvider
{
    private const string UsageUrl = "https://chatgpt.com/backend-api/wham/usage";

    private readonly HttpClient _http;

    public CodexProvider(HttpClient http) => _http = http;

    public string ToolName => "Codex";

    public async Task<UsageSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        var credentials = CodexCredentials.Read();

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
            var (plan, windows) = await FetchUsageAsync(token, credentials.AccountId, cancellationToken);
            return new UsageSnapshot
            {
                ToolName = ToolName,
                Plan = plan,
                Windows = windows,
                CapturedAt = DateTimeOffset.Now,
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Debug.WriteLine($"[Gauge] CodexProvider usage fetch failed: {ex.Message}");
            throw;
        }
    }

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
            if (ParseWindow(rateLimit, "primary_window", UsageWindowType.FiveHour, "5시간") is { } fiveHour)
            {
                windows.Add(fiveHour);
            }
            if (ParseWindow(rateLimit, "secondary_window", UsageWindowType.Weekly, "주간") is { } weekly)
            {
                windows.Add(weekly);
            }
        }

        return (plan, windows);
    }

    /// <summary>
    /// Parses one rate-limit window: <c>{ "used_percent": 0–100, "reset_at": epochSeconds }</c>.
    /// </summary>
    private static UsageWindow? ParseWindow(JsonElement rateLimit, string property, UsageWindowType type, string label)
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
            Label = label,
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
