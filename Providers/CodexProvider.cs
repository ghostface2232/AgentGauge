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
    private const string ResetCreditsUrl = "https://chatgpt.com/backend-api/wham/rate-limit-reset-credits";
    private static readonly TimeSpan ResetCreditRefreshInterval = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan ResetCreditTimeout = TimeSpan.FromSeconds(4);
    private readonly RateLimitGate _creditGate;
    private string? _creditIdentity;
    private int? _creditBalance;
    private long _lastCreditRead;
    private IReadOnlyList<ResetCreditDeadline>? _creditDeadlines;

    private static readonly UsageFetchPolicy Policy = new()
    {
        // Same escalation as Claude when the server sends no Retry-After: 2, 4, 8, 16,
        // 30(cap) minutes. The first step sits under the 3-minute periodic cadence, so
        // an isolated 429 costs at most one popover-open freshness window.
        RetryBackoff = new BackoffPolicy(TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(30)),
    };

    // The Pro tiers are shown by their marketed monthly price. The API names them
    // prolite/pro/promax (the Codex CLI's PlanType enum, 0.166), which read as internal ids.
    private static readonly IReadOnlyDictionary<string, string> KnownPlans = new Dictionary<string, string>
    {
        ["plus"] = "Plus",
        ["prolite"] = "Pro 100",
        ["pro"] = "Pro 200",
        ["promax"] = "Pro 500",
        ["free"] = "Free",
        ["go"] = "Go",
        ["business"] = "Business",
        ["team"] = "Team",
        ["enterprise"] = "Enterprise",
    };

    private readonly HttpClient _http;
    private readonly ICredentialSource _credentials;
    private readonly ICliLocator? _appServerLocator;

    public CodexProvider(
        HttpClient http,
        ICredentialSource credentials,
        IDelegatedTokenRefresher? refresher = null,
        TimeProvider? time = null,
        ICliLocator? appServerLocator = null)
        : base(credentials, Policy, refresher, time)
    {
        _http = http;
        _credentials = credentials;
        _appServerLocator = appServerLocator;
        _creditGate = new RateLimitGate(new BackoffPolicy(ResetCreditRefreshInterval, TimeSpan.FromMinutes(30)), Time);
    }

    public override ToolKind Tool => ToolKind.Codex;

    private const string RateLimitsRequest = """
        {"id":1,"method":"initialize","params":{"clientInfo":{"name":"agentgauge","version":"1.0"}}}
        {"method":"initialized"}
        {"id":2,"method":"account/rateLimits/read"}
        """ + "\n";

    protected override async Task<UsageSnapshot> FetchSnapshotAsync(
        ToolCredential credential, FetchInteraction interaction, CancellationToken cancellationToken)
    {
        try
        {
            return await FetchSnapshotAsync(credential, cancellationToken);
        }
        catch (Exception error) when (interaction == FetchInteraction.UserInitiated
            && _appServerLocator is not null && error is UsageResponseShapeException or JsonException)
        {
            if (_appServerLocator.Find("codex") is { } executable
                && (await _credentials.ReadAsync(Tool, cancellationToken)).Credential == credential)
            {
                var reply = await CliUsageProbe.RunAsync(executable,
                    "-c cli_auth_credentials_store=file -s read-only -a never app-server",
                    TimeSpan.FromSeconds(12), cancellationToken, RateLimitsRequest, 2);
                if (reply is not null && ParseAppServer(reply, credential.AccountId) is { } payload
                    && (await _credentials.ReadAsync(Tool, cancellationToken)).Credential == credential)
                    return Snapshot(payload.Plan, payload.Windows, payload.Credits) with
                    {
                        ResetCreditDeadlines = payload.Deadlines,
                    };
            }
            throw;
        }
    }

    internal static (string? Plan, IReadOnlyList<UsageWindow> Windows, int? Credits,
        IReadOnlyList<ResetCreditDeadline>? Deadlines)? ParseAppServer(string reply, string? accountId = null)
    {
        using var document = JsonDocument.Parse(reply);
        if (document.RootElement.GetObjectOrNull("result") is not { } result) return null;
        if (accountId is not null && result.GetStringOrNull("accountId") is { } reportedId && reportedId != accountId)
            return null;
        var byId = result.GetObjectOrNull("rateLimitsByLimitId");
        var legacy = result.GetObjectOrNull("rateLimits");
        var legacyId = ProviderText.Normalize(legacy?.GetStringOrNull("limitId"));
        var account = byId?.GetObjectOrNull("codex") ?? (legacyId is null or "codex" ? legacy : null);

        var windows = new List<UsageWindow>();
        var seen = new HashSet<string>();
        void AddWindows(JsonElement entry, string? prefix = null, string? label = null)
        {
            foreach (var (key, type) in new[] { ("primary", UsageWindowType.FiveHour), ("secondary", UsageWindowType.Weekly) })
                if (ParseWindow(entry, key, type, prefix, label, appServer: true) is { } window && seen.Add(window.Key))
                    windows.Add(window);
        }

        void AddScopedWindows(string id, JsonElement entry)
        {
            var slug = ProviderText.Slug(id);
            if (slug.Length > 0)
                AddWindows(entry, $"codex-additional-{slug}",
                    AdditionalLimitDisplayName(ProviderText.Normalize(entry.GetStringOrNull("limitName")) ?? id));
        }

        if (account is { } rateLimit) AddWindows(rateLimit);
        if (byId is { } additional)
            foreach (var entry in additional.EnumerateObject())
                if (entry.Name != "codex") AddScopedWindows(entry.Name, entry.Value);
        if (legacy is { } legacyRateLimit && legacyId is not (null or "codex"))
            AddScopedWindows(legacyId, legacyRateLimit);

        if (windows.Count == 0) return null;
        var creditInfo = result.GetObjectOrNull("rateLimitResetCredits");
        var credits = creditInfo?.GetInt64OrNull("availableCount") is >= 0 and var count
            ? (int?)Math.Min(count, int.MaxValue) : null;
        var deadlines = new HashSet<ResetCreditDeadline>();
        if (creditInfo is { } info && info.TryGetArray("credits", out var entries))
            foreach (var entry in entries.EnumerateArray())
                if (entry.GetStringOrNull("status") == "available"
                    && UnixTime(entry.GetInt64OrNull("expiresAt")) is { } expiry)
                    deadlines.Add(new(expiry));

        return (ProviderText.PlanLabel(account?.GetStringOrNull("planType") ?? legacy?.GetStringOrNull("planType"), KnownPlans),
            windows, credits, deadlines.Count > 0 ? deadlines.ToArray() : null);
    }

    protected override async Task<UsageSnapshot> FetchSnapshotAsync(
        ToolCredential credential, CancellationToken cancellationToken)
    {
        using var request = CreateRequest(UsageUrl, credential);

        // ResponseContentRead keeps HttpClient.Timeout over the body as well as the headers.
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken);
        EnsureUsageSuccess(response);

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, default, cancellationToken);
        var root = document.RootElement;
        UsageResponseShapeException.ThrowUnlessAnyProperty(root, ToolName, "rate_limit", "additional_rate_limits");
        if (HasUnexpectedType(root, "rate_limit", JsonValueKind.Object)
            || HasUnexpectedType(root, "additional_rate_limits", JsonValueKind.Array))
            throw new UsageResponseShapeException(ToolName);

        var plan = ProviderText.PlanLabel(root.GetStringOrNull("plan_type"), KnownPlans);

        var windows = new List<UsageWindow>();
        if (root.GetObjectOrNull("rate_limit") is { } rateLimit)
        {
            if (HasUnexpectedType(rateLimit, "primary_window", JsonValueKind.Object)
                || HasUnexpectedType(rateLimit, "secondary_window", JsonValueKind.Object))
                throw new UsageResponseShapeException(ToolName);
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

        // A window was reported but none could be read (a new duration, a renamed field). An
        // empty success would wipe the card's last good values, so it counts as a failed read.
        if (windows.Count == 0
            && root.GetObjectOrNull("rate_limit") is { } reported
            && (reported.GetObjectOrNull("primary_window") is not null
                || reported.GetObjectOrNull("secondary_window") is not null))
        {
            throw new UsageResponseShapeException(ToolName);
        }

        var credits = ParseResetCredits(root);
        var deadlines = await ReadCreditDeadlinesAsync(credential, credits, cancellationToken);
        return Snapshot(plan, windows, credits) with { ResetCreditDeadlines = deadlines };
    }

    private static bool HasUnexpectedType(JsonElement element, string key, JsonValueKind expected) =>
        element.TryGetProperty(key, out var value) && value.ValueKind != JsonValueKind.Null && value.ValueKind != expected;

    private static HttpRequestMessage CreateRequest(string url, ToolCredential credential)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {credential.AccessToken}");
        request.Headers.TryAddWithoutValidation("User-Agent", "Gauge/1.0");
        if (!string.IsNullOrEmpty(credential.AccountId))
            request.Headers.TryAddWithoutValidation("ChatGPT-Account-Id", credential.AccountId);
        return request;
    }

    private async Task<IReadOnlyList<ResetCreditDeadline>?> ReadCreditDeadlinesAsync(
        ToolCredential credential, int? credits, CancellationToken cancellationToken)
    {
        var identity = credential.HistoryIdentity
            ?? UsageIdentity.Hash(null, $"{credential.AccountId}\0{credential.AccessToken}");
        if (_creditIdentity != identity)
        {
            _creditIdentity = identity;
            _creditBalance = null;
            _creditGate.Reset();
        }

        if (credits is not > 0)
        {
            _creditBalance = null;
            _creditDeadlines = null;
            return null;
        }

        if (_creditBalance == credits && Time.GetElapsedTime(_lastCreditRead) < ResetCreditRefreshInterval)
            return _creditDeadlines;
        if (_creditGate.InCooldown(Time.GetTimestamp())) return null;

        // The supplemental endpoint is optional and runs at a lower cadence than quota polling.
        _creditBalance = credits;
        _lastCreditRead = Time.GetTimestamp();
        _creditDeadlines = null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ResetCreditTimeout);
        try
        {
            using var request = CreateRequest(ResetCreditsUrl, credential);
            using var response = await _http.SendAsync(request, timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                if (RateLimitGate.IsRetryable(response.StatusCode))
                    _creditGate.RecordFailure(RetryAfterOf(response));
                DiagnosticsLog.Write("provider", $"Codex reset deadlines unavailable: HTTP {(int)response.StatusCode}");
                return null;
            }

            _creditGate.RecordSuccess();
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: timeout.Token);
            if (!document.RootElement.TryGetArray("credits", out var entries)) return null;

            var deadlines = new HashSet<ResetCreditDeadline>();
            foreach (var entry in entries.EnumerateArray())
                if (entry.GetStringOrNull("status") == "available"
                    && entry.GetDateTimeOffsetOrNull("expires_at") is { } expires)
                    deadlines.Add(new(expires));

            _creditDeadlines = deadlines.ToArray();
            return _creditDeadlines;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException
            || ex is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            DiagnosticsLog.Write("provider", $"Codex reset deadlines unavailable: {ex.GetType().Name}");
            return null;
        }
    }

    /// <summary>
    /// Codex accounts can hold a number of manual rate-limit resets:
    /// <c>rate_limit_reset_credits: { available_count, applicable_available_count }</c>.
    /// <c>available_count</c> is the balance held; <c>applicable_available_count</c> is the
    /// subset spendable against the limit currently in force, so it reads 0 whenever nothing
    /// is capped — the held balance is what manual-reset inference needs. The object is absent on plans
    /// without the feature, which stays null rather than becoming a zero balance.
    /// </summary>
    private static int? ParseResetCredits(JsonElement root)
        => root.GetObjectOrNull("rate_limit_reset_credits")?.GetInt64OrNull("available_count")
            is { } count && count >= 0
            ? (int)Math.Min(count, int.MaxValue)
            : null;

    /// <summary>
    /// Parses one rate-limit window: <c>{ "used_percent": 0–100, "reset_at": epochSeconds }</c>.
    /// </summary>
    private static UsageWindow? ParseWindow(
        JsonElement rateLimit,
        string property,
        UsageWindowType fallbackType,
        string? idPrefix = null,
        string? groupLabel = null,
        bool appServer = false)
    {
        if (rateLimit.GetObjectOrNull(property) is not { } window
            || window.GetDoubleOrNull(appServer ? "usedPercent" : "used_percent") is not { } usedPercent)
        {
            return null;
        }

        var resetTime = UnixTime(window.GetInt64OrNull(appServer ? "resetsAt" : "reset_at"));
        var durationKey = appServer ? "windowDurationMins" : "limit_window_seconds";
        var durationSeconds = window.GetInt64OrNull(durationKey);
        if (appServer)
            durationSeconds = durationSeconds is >= 0 and <= long.MaxValue / 60 ? durationSeconds * 60 : null;
        UsageWindowType type;
        TimeSpan? duration;
        if (window.TryGetProperty(durationKey, out var rawDuration) && (!appServer || rawDuration.ValueKind != JsonValueKind.Null))
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

    private static DateTimeOffset? UnixTime(long? epoch) =>
        epoch is >= -62135596800 and <= 253402300799 ? DateTimeOffset.FromUnixTimeSeconds(epoch.Value) : null;

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
