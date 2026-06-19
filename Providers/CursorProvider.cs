using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using Gauge.Localization;
using Gauge.Models;
using Gauge.Providers.Internal;
using Gauge.Services;

namespace Gauge.Providers;

/// <summary>
/// Reads Cursor usage from <c>GET https://cursor.com/api/usage-summary</c> using the
/// session token Cursor stores locally (see <see cref="CursorCredentialSource"/>). The
/// token + user id form Cursor's web-session cookie
/// (<c>WorkosCursorSessionToken=&lt;userId&gt;::&lt;token&gt;</c>).
///
/// Cursor bills by credit consumption over a billing cycle rather than rolling 5h/weekly
/// windows, so usage is presented as a single percentage bar (plan utilization) with the
/// billing-cycle end as its reset.
/// </summary>
public sealed class CursorProvider : IUsageProvider
{
    private const string UsageUrl = "https://cursor.com/api/usage-summary";

    private readonly HttpClient _http;
    private readonly ICredentialSource _credentials;

    public CursorProvider(HttpClient http, ICredentialSource credentials)
    {
        _http = http;
        _credentials = credentials;
    }

    public ToolKind Tool => ToolKind.Cursor;
    public string ToolName => ToolCatalog.For(ToolKind.Cursor).DisplayName;

    public async Task<UsageSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        var credentialResult = await _credentials.ReadAsync(ToolKind.Cursor, cancellationToken);
        var credentials = credentialResult.Credential;

        if (credentialResult.Status == CredentialReadStatus.Invalid)
        {
            throw new AuthenticationRequiredException(ToolKind.Cursor, HttpStatusCode.Unauthorized);
        }

        // Not logged in: a clean "no data yet" state, not a failure.
        if (credentials?.AccessToken is not { Length: > 0 } token
            || credentials.AccountId is not { Length: > 0 } userId)
        {
            return Empty();
        }

        try
        {
            var (plan, window) = await FetchUsageAsync(userId, token, cancellationToken);
            return new UsageSnapshot
            {
                ToolName = ToolName,
                Plan = plan,
                Windows = window is null ? Array.Empty<UsageWindow>() : new[] { window },
                CapturedAt = DateTimeOffset.Now,
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (ex is HttpRequestException { StatusCode: HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden } httpError)
            {
                throw new AuthenticationRequiredException(ToolKind.Cursor, httpError.StatusCode!.Value);
            }
            Debug.WriteLine($"[Gauge] CursorProvider usage fetch failed: {ex.Message}");
            throw;
        }
    }

    private async Task<(string? Plan, UsageWindow? Window)> FetchUsageAsync(
        string userId, string token, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, UsageUrl);
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        request.Headers.TryAddWithoutValidation("Cookie", $"WorkosCursorSessionToken={userId}%3A%3A{token}");

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, default, cancellationToken);
        var root = document.RootElement;

        var plan = MapPlan(root.GetStringOrNull("membershipType"));
        var percentUsed = ParsePlanPercentUsed(root);
        var resetTime = ParseBillingCycleEnd(root.GetStringOrNull("billingCycleEnd"));

        var window = new UsageWindow
        {
            Type = UsageWindowType.BillingCycle,
            UsedRatio = Math.Clamp(percentUsed / 100.0, 0.0, 1.0),
            Label = WindowLabels.For(UsageWindowType.BillingCycle),
            ResetTime = resetTime,
        };
        return (plan, window);
    }

    /// <summary>
    /// Headline usage percent, mirroring Cursor's dashboard precedence:
    /// plan.totalPercentUsed → avg(auto, api) → either lane → plan used/limit →
    /// overall (personal cap) → pooled (team). All values are already in percent units.
    /// </summary>
    private static double ParsePlanPercentUsed(JsonElement root)
    {
        var individual = root.GetObjectOrNull("individualUsage");
        var plan = individual?.GetObjectOrNull("plan");

        if (plan?.GetDoubleOrNull("totalPercentUsed") is { } total)
        {
            return Clamp(total);
        }

        var auto = plan?.GetDoubleOrNull("autoPercentUsed");
        var api = plan?.GetDoubleOrNull("apiPercentUsed");
        if (auto is { } a && api is { } b)
        {
            return Clamp((a + b) / 2.0);
        }
        if (api is { } apiOnly)
        {
            return Clamp(apiOnly);
        }
        if (auto is { } autoOnly)
        {
            return Clamp(autoOnly);
        }

        if (RatioPercent(plan) is { } planRatio)
        {
            return planRatio;
        }
        if (RatioPercent(individual?.GetObjectOrNull("overall")) is { } overallRatio)
        {
            return overallRatio;
        }
        if (RatioPercent(root.GetObjectOrNull("teamUsage")?.GetObjectOrNull("pooled")) is { } pooledRatio)
        {
            return pooledRatio;
        }

        return 0;
    }

    /// <summary>used/limit as a clamped percentage, or null when the block/limit is absent.</summary>
    private static double? RatioPercent(JsonElement? block)
    {
        if (block?.GetDoubleOrNull("limit") is not { } limit || limit <= 0)
        {
            return null;
        }
        var used = block?.GetDoubleOrNull("used") ?? 0;
        return Clamp(used / limit * 100.0);
    }

    private static double Clamp(double value) => Math.Clamp(value, 0.0, 100.0);

    private static DateTimeOffset? ParseBillingCycleEnd(string? value) =>
        DateTimeOffset.TryParse(value, out var parsed) ? parsed : null;

    private static string? MapPlan(string? membershipType) => membershipType?.ToLowerInvariant() switch
    {
        null or "" => null,
        "free" => "Free",
        "hobby" => "Hobby",
        "pro" => "Pro",
        "pro_plus" or "pro-plus" => "Pro+",
        "ultra" => "Ultra",
        "business" => "Business",
        "team" => "Team",
        "enterprise" => "Enterprise",
        var other => char.ToUpperInvariant(other[0]) + other[1..],
    };

    private UsageSnapshot Empty() => new()
    {
        ToolName = ToolName,
        Plan = null,
        Windows = Array.Empty<UsageWindow>(),
        CapturedAt = DateTimeOffset.Now,
    };
}
