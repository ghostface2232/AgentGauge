using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using Gauge.Models;
using Gauge.Providers.Internal;
using Gauge.Services;

namespace Gauge.Providers;

/// <summary>
/// Reads Claude Code usage from Anthropic's official OAuth usage endpoint
/// (<c>GET https://api.anthropic.com/api/oauth/usage</c>) using the OAuth token the
/// CLI stores in <c>~/.claude/.credentials.json</c>. This returns the same real
/// figures Claude Code's <c>/usage</c> shows — actual 5-hour and weekly utilization
/// (0–100) and real reset times — unlike token-counting tools such as ccusage.
///
/// RATE LIMITING: this endpoint is throttled hard. Measured behavior is ~3 reads in a
/// short window, then 429 with a penalty cooldown (and no Retry-After header), and the
/// bucket is shared per account/IP — so over-polling here also starves the real CLI.
/// To stay well under that, the provider is stateful:
///   • it hits the network at most once per <see cref="MinFetchInterval"/> and serves
///     its last good snapshot in between (so the coordinator's 60s cycle and every
///     popover-open forced refresh do NOT each make a call);
///   • on 429 it backs off exponentially (<see cref="BaseCooldown"/>…<see cref="MaxCooldown"/>);
///   • while throttled/cooling down it returns the cached snapshot as a success, so the
///     card keeps showing the last good value instead of flipping to "no data".
/// It also sends the <c>claude-code</c> User-Agent, which the endpoint buckets less
/// aggressively than arbitrary agents.
///
/// The plan label (Max 5x/20x, Pro, …) comes from the credentials file, so it is
/// reported even before the first successful usage call.
/// </summary>
public sealed class ClaudeProvider : IUsageProvider
{
    private const string UsageUrl = "https://api.anthropic.com/api/oauth/usage";

    // Required beta header for the OAuth usage endpoint.
    private const string OAuthBetaHeader = "oauth-2025-04-20";

    // The endpoint buckets the claude-code product agent more leniently than others.
    private const string UserAgent = "claude-code/2.1.179";

    // Don't touch the network more often than this on the happy path; 5h/weekly
    // windows move slowly, so this is plenty granular and keeps us far under the limit.
    private static readonly TimeSpan MinFetchInterval = TimeSpan.FromMinutes(5);

    // 429 backoff (no Retry-After is sent, so we pick our own schedule): doubles per
    // consecutive 429 up to the cap.
    private static readonly TimeSpan BaseCooldown = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan MaxCooldown = TimeSpan.FromMinutes(30);

    private readonly HttpClient _http;
    private readonly ICredentialSource _credentials;

    // Only ever accessed from the coordinator's serialized refresh (one call at a
    // time), so no locking is needed.
    private UsageSnapshot? _lastSnapshot;
    private long _lastSuccessTick;
    private long _cooldownUntilTick;
    private int _consecutive429;
    private string? _credentialFingerprint;
    private bool _credentialFingerprintInitialized;

    public ClaudeProvider(HttpClient http, ICredentialSource credentials)
    {
        _http = http;
        _credentials = credentials;
    }

    public string ToolName => "Claude Code";

    public async Task<UsageSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        var credentialResult = await _credentials.ReadAsync(ToolKind.ClaudeCode, cancellationToken);
        var credentials = credentialResult.Credential;
        var now = Environment.TickCount64;

        // A CLI re-login/account switch must not serve the prior account's 5-minute
        // cache. Keep only a one-way fingerprint, never the token itself.
        var fingerprint = credentials?.AccessToken is { Length: > 0 } accessToken
            ? Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(accessToken)))
            : null;
        if (_credentialFingerprintInitialized && !StringComparer.Ordinal.Equals(_credentialFingerprint, fingerprint))
        {
            _lastSnapshot = null;
            _lastSuccessTick = 0;
            _cooldownUntilTick = 0;
            _consecutive429 = 0;
        }
        _credentialFingerprint = fingerprint;
        _credentialFingerprintInitialized = true;

        if (credentialResult.Status == CredentialReadStatus.Invalid)
        {
            throw new AuthenticationRequiredException(ToolKind.ClaudeCode, HttpStatusCode.Unauthorized);
        }

        // Serve the cached snapshot without a network call when we fetched recently or
        // are in a 429 cooldown. Refresh the (cheap, file-based) plan label so a plan
        // change still shows promptly.
        var inCooldown = _cooldownUntilTick != 0 && now < _cooldownUntilTick;
        var fetchedRecently = _lastSuccessTick != 0 && now - _lastSuccessTick < MinFetchInterval.TotalMilliseconds;
        if (_lastSnapshot is not null && (inCooldown || fetchedRecently))
        {
            return _lastSnapshot with { Plan = credentials?.Plan ?? _lastSnapshot.Plan };
        }

        if (credentials?.AccessToken is not { Length: > 0 } token)
        {
            // No usable token: report the plan (if known) with no windows. Don't throw,
            // so this reads as "no data yet" rather than a transient failure.
            return new UsageSnapshot
            {
                ToolName = ToolName,
                Plan = credentials?.Plan,
                Windows = Array.Empty<UsageWindow>(),
                CapturedAt = DateTimeOffset.Now,
            };
        }

        try
        {
            var windows = await FetchWindowsAsync(token, cancellationToken);
            _consecutive429 = 0;
            _cooldownUntilTick = 0;
            _lastSuccessTick = Environment.TickCount64;
            _lastSnapshot = new UsageSnapshot
            {
                ToolName = ToolName,
                Plan = credentials.Plan,
                Windows = windows,
                CapturedAt = DateTimeOffset.Now,
            };
            return _lastSnapshot;
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.TooManyRequests)
        {
            _consecutive429++;
            var cooldown = NextCooldown(_consecutive429);
            _cooldownUntilTick = Environment.TickCount64 + (long)cooldown.TotalMilliseconds;
            Debug.WriteLine($"[Gauge] ClaudeProvider 429 (x{_consecutive429}); backing off {cooldown.TotalMinutes:0}m");

            // Keep showing the last good value if we have one; only surface a failure
            // on a cold start with nothing cached.
            if (_lastSnapshot is not null)
            {
                return _lastSnapshot with { Plan = credentials.Plan ?? _lastSnapshot.Plan };
            }
            throw;
        }
        catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw new AuthenticationRequiredException(ToolKind.ClaudeCode, ex.StatusCode!.Value);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Debug.WriteLine($"[Gauge] ClaudeProvider usage fetch failed: {ex.Message}");
            if (_lastSnapshot is not null)
            {
                return _lastSnapshot with { Plan = credentials.Plan ?? _lastSnapshot.Plan };
            }
            throw;
        }
    }

    private static TimeSpan NextCooldown(int consecutive429)
    {
        // 2, 4, 8, 16, 30(cap), 30, … minutes.
        var shift = Math.Min(consecutive429 - 1, 4);
        var ticks = Math.Min(MaxCooldown.Ticks, BaseCooldown.Ticks * (1L << shift));
        return TimeSpan.FromTicks(ticks);
    }

    private async Task<List<UsageWindow>> FetchWindowsAsync(string token, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, UsageUrl);
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");
        request.Headers.TryAddWithoutValidation("anthropic-beta", OAuthBetaHeader);
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, default, cancellationToken);
        var root = document.RootElement;

        var windows = new List<UsageWindow>();
        if (ParseWindow(root, "five_hour", UsageWindowType.FiveHour, "5시간") is { } fiveHour)
        {
            windows.Add(fiveHour);
        }
        if (ParseWindow(root, "seven_day", UsageWindowType.Weekly, "주간") is { } weekly)
        {
            windows.Add(weekly);
        }

        return windows;
    }

    /// <summary>
    /// Parses one window object: <c>{ "utilization": 0–100, "resets_at": ISO8601 }</c>.
    /// A null/absent object (or null utilization) means the window has no data and is omitted.
    /// </summary>
    private static UsageWindow? ParseWindow(JsonElement root, string property, UsageWindowType type, string label)
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
            Label = label,
            ResetTime = window.GetDateTimeOffsetOrNull("resets_at"),
        };
    }
}
