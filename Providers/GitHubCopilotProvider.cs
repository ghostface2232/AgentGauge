using System.Net.Http;
using System.Text.Json;
using Gauge.Localization;
using Gauge.Models;
using Gauge.Providers.Internal;
using Gauge.Services;

namespace Gauge.Providers;

/// <summary>
/// Reads GitHub Copilot usage from GitHub's internal endpoint
/// <c>GET https://api.github.com/copilot_internal/user</c> using the GitHub OAuth token a local
/// client already stores (see <see cref="GitHubCopilotCredentialSource"/>). This is the same
/// endpoint the official editor integrations use to show the premium-request quota; it is
/// undocumented and unversioned, so parsing is tolerant — every field is read defensively and a
/// quota without a usable fraction is skipped, never assumed spent (AGENTS: parse a live response,
/// not a remembered schema).
///
/// Copilot meters monthly quotas (chat, code completions, premium requests) rather than rolling
/// 5h/weekly windows, so each metered quota becomes one
/// <see cref="UsageWindowType.BillingCycle"/> window, keyed by its quota id and resetting on the
/// account's monthly reset date. Fetch failures propagate (via the shared layer's
/// <see cref="UsageFetchPolicy.None"/>) so the coordinator keeps the last good snapshot.
/// </summary>
public sealed class GitHubCopilotProvider : UsageProviderBase
{
    private const string UsageUrl = "https://api.github.com/copilot_internal/user";

    private static readonly IReadOnlyDictionary<string, string> KnownPlans = new Dictionary<string, string>
    {
        ["individual"] = "Pro",
        ["business"] = "Business",
        ["enterprise"] = "Enterprise",
        ["free"] = "Free",
    };

    private readonly HttpClient _http;

    public GitHubCopilotProvider(HttpClient http, ICredentialSource credentials, TimeProvider? time = null)
        : base(credentials, UsageFetchPolicy.None, refresher: null, time)
    {
        _http = http;
    }

    public override ToolKind Tool => ToolKind.GitHubCopilot;

    protected override async Task<UsageSnapshot> FetchSnapshotAsync(
        ToolCredential credential, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, UsageUrl);
        request.Headers.TryAddWithoutValidation("Authorization", $"token {credential.AccessToken}");
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        // The endpoint expects an editor identity; mirror the official Copilot client. This is
        // the one place — like Claude's claude-code User-Agent — we deliberately match a known
        // client, for interop, not deception.
        request.Headers.TryAddWithoutValidation("Editor-Version", "vscode/1.100.0");
        request.Headers.TryAddWithoutValidation("Editor-Plugin-Version", "copilot-chat/0.26.0");
        request.Headers.TryAddWithoutValidation("User-Agent", "GitHubCopilotChat/0.26.0");

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        EnsureUsageSuccess(response);

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, default, cancellationToken);
        var root = document.RootElement;

        var plan = MapPlan(root.GetStringOrNull("copilot_plan"), root.GetStringOrNull("access_type_sku"));
        var windows = ParseWindows(root);
        return Snapshot(plan, windows);
    }

    /// <summary>
    /// Builds one BillingCycle window per metered quota in <c>quota_snapshots</c>. A quota that
    /// is unlimited or carries no entitlement (<c>has_quota=false</c>) is skipped — it is not a
    /// meaningful gauge — never assumed spent. The reset is the account-level
    /// <c>quota_reset_date_utc</c>; the per-quota <c>quota_reset_at</c> is unused (0 in practice).
    /// </summary>
    private static IReadOnlyList<UsageWindow> ParseWindows(JsonElement root)
    {
        if (root.GetObjectOrNull("quota_snapshots") is not { } snapshots)
        {
            return Array.Empty<UsageWindow>();
        }

        // GetDateTimeOffsetOrNull parses with InvariantCulture, not the UI language's culture,
        // and reads an offset-less value as UTC. Captured responses carry a full Z timestamp,
        // but the field is undocumented and its name says UTC, so a bare date must not be
        // read as local time — that would move the reset by the user's own offset.
        var reset = root.GetDateTimeOffsetOrNull("quota_reset_date_utc");
        var windows = new List<UsageWindow>();
        foreach (var quota in snapshots.EnumerateObject())
        {
            var snap = quota.Value;
            if (snap.ValueKind != JsonValueKind.Object)
            {
                continue;
            }
            if (snap.GetBoolOrNull("has_quota") == false || snap.GetBoolOrNull("unlimited") == true)
            {
                continue;
            }
            if (UsedRatio(snap) is not { } used)
            {
                continue;
            }

            // Absolute counts ("128 / 300 requests") when the response carries them. These are
            // request counts, not tokens; the model's UsedTokens/LimitTokens are generic
            // counters. Both fields are required — remaining alone can't produce a denominator.
            long? usedCount = null, limitCount = null;
            if (snap.GetDoubleOrNull("entitlement") is { } entitlement && entitlement > 0
                && snap.GetDoubleOrNull("remaining") is { } remaining)
            {
                limitCount = (long)Math.Round(entitlement);
                usedCount = (long)Math.Round(Math.Clamp(entitlement - remaining, 0, entitlement));
            }

            // Every Copilot quota is a BillingCycle window, so the type-derived label would
            // read "Usage" for all of them. Carry the per-quota label KEY on the window so
            // the rehydrated cache and the toast titles resolve the same distinct name.
            var labelKey = QuotaLabelKey(quota.Name);
            windows.Add(new UsageWindow
            {
                Type = UsageWindowType.BillingCycle,
                // Distinct ids (chat / completions / premium_interactions) keep the several
                // BillingCycle windows apart for reconciliation, notifications, and the cache.
                Id = quota.Name,
                LabelKey = labelKey,
                Label = WindowLabels.For(UsageWindowType.BillingCycle, labelKey),
                UsedRatio = used,
                ResetTime = reset,
                UsedTokens = usedCount,
                LimitTokens = limitCount,
            });
        }
        return windows;
    }

    /// <summary>Fraction used in [0,1]: from <c>percent_remaining</c>, else <c>remaining/entitlement</c>.</summary>
    private static double? UsedRatio(JsonElement snap)
    {
        if (snap.GetDoubleOrNull("percent_remaining") is { } percentRemaining)
        {
            return Math.Clamp(1.0 - percentRemaining / 100.0, 0.0, 1.0);
        }
        // Require BOTH fields: a missing `remaining` means "unknown", not zero. Defaulting it
        // to 0 would read as 100% used and fire a false danger alert — a partial response or a
        // schema drift on this undocumented endpoint must skip the window, never assume it spent.
        if (snap.GetDoubleOrNull("entitlement") is { } entitlement && entitlement > 0
            && snap.GetDoubleOrNull("remaining") is { } remaining)
        {
            return Math.Clamp(1.0 - remaining / entitlement, 0.0, 1.0);
        }
        return null;
    }

    /// <summary>
    /// Localization key for a quota's label. An unknown future quota returns its raw id,
    /// which <c>Loc.Get</c> passes through unchanged — so the window shows the id rather
    /// than being hidden or collapsing into the generic "Usage".
    /// </summary>
    private static string QuotaLabelKey(string quotaId) => quotaId.ToLowerInvariant() switch
    {
        "chat" => "Label_Copilot_Chat",
        "completions" => "Label_Copilot_Completions",
        "premium_interactions" => "Label_Copilot_Premium",
        _ => quotaId,
    };

    /// <summary>
    /// Best-effort plan label. The free tier is signalled by <c>access_type_sku</c> (e.g.
    /// "free_limited_copilot"); otherwise map <c>copilot_plan</c>, where a paid individual plan is
    /// "Copilot Pro". Degrades to the raw value for an unrecognized plan rather than guessing.
    /// </summary>
    private static string? MapPlan(string? copilotPlan, string? sku)
    {
        if (sku?.Contains("free", StringComparison.OrdinalIgnoreCase) == true)
        {
            return "Free";
        }
        return ProviderText.PlanLabel(copilotPlan, KnownPlans);
    }
}
