using System.Net.Http;
using System.Text.Json;
using Gauge.Localization;
using Gauge.Models;
using Gauge.Providers.Internal;
using Gauge.Services;

namespace Gauge.Providers;

/// <summary>
/// Reads Claude Code usage from Anthropic's official OAuth usage endpoint
/// (<c>GET https://api.anthropic.com/api/oauth/usage</c>) using the OAuth token the
/// CLI stores in <c>~/.claude/.credentials.json</c>. This returns the same real
/// figures Claude Code's <c>/usage</c> shows — actual 5-hour, weekly, and model-scoped
/// weekly utilization (0–100) and real reset times — unlike token-counting tools such
/// as ccusage. Newer responses expose scoped limits such as Fable through <c>limits[]</c>.
///
/// RATE LIMITING: this endpoint is throttled hard. Measured behavior is ~3 reads in a
/// short window, then 429 with a penalty cooldown (and no Retry-After header), and the
/// bucket is shared per account/IP — so over-polling here also starves the real CLI.
/// The shared fetch layer (<see cref="UsageProviderBase"/>) therefore runs with the
/// strictest policy: at most one network call per 5 minutes on the happy path, the
/// 2→4→…→30-minute cooldown escalation on retryable statuses (Retry-After would win if
/// Anthropic ever sent one), and the cached snapshot served on any failure so the card
/// keeps its last good value instead of flipping to "no data". It also sends the
/// <c>claude-code</c> User-Agent, which the endpoint buckets less aggressively than
/// arbitrary agents.
///
/// The plan label (Max 5x/20x, Pro, …) comes from the credentials file, so it is
/// reported even before the first successful usage call.
///
/// EXPIRED TOKEN: the CLI's access token lives only a few hours, so after an overnight
/// boot it is expired before Claude Code is ever launched. When an <see cref="IDelegatedTokenRefresher"/>
/// is supplied, a stale/rejected token triggers a delegated refresh (the CLI refreshes
/// its own token) and a re-read, so usage works without first opening Claude Code.
/// </summary>
public sealed class ClaudeProvider : UsageProviderBase
{
    private const string UsageUrl = "https://api.anthropic.com/api/oauth/usage";

    // Required beta header for the OAuth usage endpoint.
    private const string OAuthBetaHeader = "oauth-2025-04-20";

    // The endpoint buckets the claude-code product agent more leniently than others.
    private const string UserAgent = "claude-code/2.1.179";

    private static readonly UsageFetchPolicy Policy = new()
    {
        // 5h/weekly windows move slowly, so this cap is plenty granular and keeps us
        // far under the endpoint's limit.
        MinFetchInterval = TimeSpan.FromMinutes(5),
        // No Retry-After is sent, so this schedule picks the cooldowns: 2, 4, 8, 16,
        // 30(cap), 30, … minutes per consecutive retryable failure.
        RetryBackoff = new BackoffPolicy(TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(30)),
        ServeCachedOnAnyFailure = true,
    };

    private readonly HttpClient _http;

    public ClaudeProvider(
        HttpClient http,
        ICredentialSource credentials,
        IDelegatedTokenRefresher? refresher = null,
        TimeProvider? time = null)
        : base(credentials, Policy, refresher, time)
    {
        _http = http;
    }

    public override ToolKind Tool => ToolKind.ClaudeCode;

    protected override string? PlanFromCredential(ToolCredential? credential) => credential?.Plan;

    protected override async Task<UsageSnapshot> FetchSnapshotAsync(
        ToolCredential credential, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, UsageUrl);
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {credential.AccessToken}");
        request.Headers.TryAddWithoutValidation("anthropic-beta", OAuthBetaHeader);
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        EnsureUsageSuccess(response);

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, default, cancellationToken);
        var root = document.RootElement;

        var windows = new List<UsageWindow>();
        if (ParseWindow(root, "five_hour", UsageWindowType.FiveHour) is { } fiveHour)
        {
            windows.Add(fiveHour);
        }
        if (ParseWindow(root, "seven_day", UsageWindowType.Weekly) is { } weekly)
        {
            windows.Add(weekly);
        }
        windows.AddRange(ParseScopedWeeklyLimits(root));

        return Snapshot(credential.Plan, windows);
    }

    /// <summary>
    /// Parses one window object: <c>{ "utilization": 0–100, "resets_at": ISO8601 }</c>.
    /// A null/absent object (or null utilization) means the window has no data and is omitted.
    /// </summary>
    private static UsageWindow? ParseWindow(JsonElement root, string property, UsageWindowType type)
    {
        if (root.GetObjectOrNull(property) is not { } window
            || window.GetDoubleOrNull("utilization") is not { } utilization)
        {
            return null;
        }

        return new UsageWindow
        {
            Type = type,
            UsedRatio = Math.Clamp(utilization / 100.0, 0.0, 1.0),
            Label = WindowLabels.For(type),
            ResetTime = window.GetDateTimeOffsetOrNull("resets_at"),
            Duration = type == UsageWindowType.FiveHour
                ? TimeSpan.FromHours(5)
                : TimeSpan.FromDays(7),
        };
    }

    /// <summary>
    /// Parses the newer additive <c>limits[]</c> shape. The account-wide session and weekly
    /// entries duplicate <c>five_hour</c>/<c>seven_day</c>, so only model-scoped weekly
    /// entries are added. Anthropic currently reports Fable this way. <c>is_active</c> is
    /// deliberately not used as a filter: live enforceable limits can report false.
    /// </summary>
    private static IReadOnlyList<UsageWindow> ParseScopedWeeklyLimits(JsonElement root)
    {
        if (!root.TryGetArray("limits", out var limits))
        {
            return Array.Empty<UsageWindow>();
        }

        var windows = new List<UsageWindow>();
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var limit in limits.EnumerateArray())
        {
            if (!string.Equals(limit.GetStringOrNull("kind"), "weekly_scoped", StringComparison.OrdinalIgnoreCase)
                || !string.Equals(limit.GetStringOrNull("group"), "weekly", StringComparison.OrdinalIgnoreCase)
                || limit.GetDoubleOrNull("percent") is not { } percent
                || !double.IsFinite(percent)
                || limit.GetObjectOrNull("scope")?.GetObjectOrNull("model") is not { } model
                || ProviderText.Normalize(model.GetStringOrNull("display_name")) is not { } modelName)
            {
                continue;
            }

            var identity = ProviderText.Normalize(model.GetStringOrNull("id")) ?? modelName;
            var slug = ProviderText.Slug(identity);
            if (slug.Length == 0 || slug == "all-models" || !seenIds.Add(slug))
            {
                continue;
            }

            windows.Add(new UsageWindow
            {
                Id = $"claude-weekly-scoped-{slug}",
                GroupLabel = modelName,
                Type = UsageWindowType.Weekly,
                UsedRatio = Math.Clamp(percent / 100.0, 0.0, 1.0),
                Label = WindowLabels.For(UsageWindowType.Weekly),
                ResetTime = limit.GetDateTimeOffsetOrNull("resets_at"),
                Duration = TimeSpan.FromDays(7),
            });
        }

        return windows;
    }
}
