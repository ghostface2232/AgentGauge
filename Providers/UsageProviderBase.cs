using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using Gauge.Models;
using Gauge.Services;

namespace Gauge.Providers;

/// <summary>
/// A usage-endpoint failure that carries the response's <c>Retry-After</c> so the
/// shared cooldown can honor the server's own schedule. It derives from
/// <see cref="HttpRequestException"/> with the status set, so callers that only match
/// on status keep working.
/// </summary>
public sealed class UsageEndpointException(HttpStatusCode statusCode, TimeSpan? retryAfter)
    : HttpRequestException($"Usage endpoint returned {(int)statusCode} ({statusCode}).", null, statusCode)
{
    public TimeSpan? RetryAfter { get; } = retryAfter;
}

/// <summary>
/// How a provider's shared fetch layer throttles and degrades. <see cref="None"/> means
/// every call goes to the network and every failure propagates (the coordinator then
/// keeps its last good snapshot and marks the card stale).
/// </summary>
public sealed record UsageFetchPolicy
{
    /// <summary>No throttling, no provider-side cache serving (Cursor, GitHub Copilot).</summary>
    public static UsageFetchPolicy None { get; } = new();

    /// <summary>
    /// Happy-path network cap: within this interval of a success, the cached snapshot is
    /// served without a call. Zero disables it. Unlike the failure cooldown this is a
    /// COST cap, so even a user-initiated refresh does not pierce it.
    /// </summary>
    public TimeSpan MinFetchInterval { get; init; }

    /// <summary>
    /// Backoff schedule for retryable statuses (<see cref="RateLimitGate.IsRetryable"/>).
    /// Non-null arms the shared cooldown gate: background fetches inside the cooldown are
    /// served from cache, user-initiated ones always go out, and a server Retry-After
    /// overrides the schedule. Null propagates those failures like any other.
    /// </summary>
    public BackoffPolicy? RetryBackoff { get; init; }

    /// <summary>
    /// Serve the cached snapshot on ANY non-auth failure (network, parse, …), not just
    /// retryable statuses. Claude opts in so its brittle endpoint never blanks the card;
    /// with this off, such failures propagate so the coordinator marks the card stale.
    /// </summary>
    public bool ServeCachedOnAnyFailure { get; init; }
}

/// <summary>
/// Shared skeleton for the HTTP usage providers (Claude, Codex, Cursor, GitHub
/// Copilot). It owns everything that is the same for each of them — reading the
/// CLI-local credential, the delegated refresh of an expired token, the
/// 401/403 → refresh → single retry recovery, the empty "not signed in" snapshot, and
/// the shared retry/cooldown layer configured by <see cref="UsageFetchPolicy"/> —
/// leaving a subclass with only its endpoint call and parsing
/// (<see cref="FetchSnapshotAsync"/>).
///
/// Cache/cooldown state is only ever touched from the coordinator's serialized refresh
/// (one call at a time), so no locking is needed.
/// </summary>
public abstract class UsageProviderBase : IUsageProvider
{
    private readonly ICredentialSource _credentials;
    private readonly IDelegatedTokenRefresher? _refresher;
    private readonly UsageFetchPolicy _policy;
    private readonly RateLimitGate? _gate;
    // Whether this provider can serve a snapshot without the network, and therefore
    // must invalidate that state when the account behind the token changes.
    private readonly bool _cachesSnapshots;

    private UsageSnapshot? _lastSnapshot;
    private long? _lastSuccessTimestamp;
    private string? _credentialFingerprint;
    private bool _credentialFingerprintInitialized;

    protected UsageProviderBase(
        ICredentialSource credentials,
        UsageFetchPolicy policy,
        IDelegatedTokenRefresher? refresher = null,
        TimeProvider? time = null)
    {
        _credentials = credentials;
        _policy = policy;
        _refresher = refresher;
        Time = time ?? TimeProvider.System;
        _gate = policy.RetryBackoff is { } backoff ? new RateLimitGate(backoff, Time) : null;
        _cachesSnapshots = _gate is not null || policy.MinFetchInterval > TimeSpan.Zero || policy.ServeCachedOnAnyFailure;
    }

    protected TimeProvider Time { get; }

    public abstract ToolKind Tool { get; }
    public string ToolName => ToolCatalog.For(Tool).DisplayName;

    public Task<UsageSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
        => GetSnapshotAsync(FetchInteraction.Background, cancellationToken);

    public async Task<UsageSnapshot> GetSnapshotAsync(FetchInteraction interaction, CancellationToken cancellationToken)
    {
        var credentialResult = await _credentials.ReadAsync(Tool, cancellationToken);

        // A locally expired token (reported Invalid) is normal after a boot: rather than
        // fail until the tool's CLI is next opened, ask the CLI to refresh its own token
        // (it owns the refresh-token rotation, so this can't break its login), then
        // re-read. Done before the fingerprint check below so a successful refresh
        // doesn't read as an account switch.
        if (credentialResult.Status == CredentialReadStatus.Invalid && _refresher is not null
            && await _refresher.TryRefreshAsync(cancellationToken))
        {
            credentialResult = await _credentials.ReadAsync(Tool, cancellationToken);
        }

        var credential = credentialResult.Credential;
        var nowTimestamp = Time.GetTimestamp();

        if (_cachesSnapshots)
        {
            TrackCredentialFingerprint(credential);
        }

        if (credentialResult.Status == CredentialReadStatus.Invalid)
        {
            throw new AuthenticationRequiredException(Tool, HttpStatusCode.Unauthorized);
        }

        // Serve the cached snapshot without a network call while inside the happy-path
        // interval or a rate-limit cooldown. Only the cooldown distinguishes who asked:
        // a user-initiated refresh pierces it (the user gets a real attempt), but not
        // the cost-capping min interval. The (cheap, file-based) plan label is still
        // refreshed so a plan change shows promptly.
        var blockedByCooldown = _gate is not null && _gate.ShouldBlock(interaction, nowTimestamp);
        var fetchedRecently = _policy.MinFetchInterval > TimeSpan.Zero
            && _lastSuccessTimestamp is { } lastSuccess
            && Time.GetElapsedTime(lastSuccess, nowTimestamp) < _policy.MinFetchInterval;
        if (_lastSnapshot is not null && (blockedByCooldown || fetchedRecently))
        {
            return WithRefreshedPlan(_lastSnapshot, credential);
        }

        // No usable token (not signed in): a legitimate "no data yet" state, not a failure.
        if (credential is null || !HasUsableCredential(credential))
        {
            return Empty(credential);
        }

        // Cooling down with nothing cached (cold start, or an account switch cleared the
        // cache): fail fast WITHOUT a network call. Falling through would hammer the
        // throttling endpoint on every cycle and popover open — the exact traffic the
        // cooldown exists to stop. The coordinator records the failure and the normal
        // cadence retries once the cooldown lapses.
        if (blockedByCooldown)
        {
            throw new HttpRequestException($"{ToolName} usage endpoint is in a rate-limit cooldown.");
        }

        try
        {
            return await FetchWithPolicyAsync(credential, cancellationToken);
        }
        catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            // The server rejected a token that looked valid locally (e.g. revoked, or it
            // expired between read and call). Try one delegated refresh and, if the CLI
            // hands us a fresh token, retry the fetch once before giving up.
            if (_refresher is not null && await _refresher.TryRefreshAsync(cancellationToken))
            {
                var refreshed = await _credentials.ReadAsync(Tool, cancellationToken);
                if (refreshed.Status == CredentialReadStatus.Available
                    && refreshed.Credential is { } freshCredential && HasUsableCredential(freshCredential))
                {
                    // Adopt the rotated token's fingerprint before the retry, or the NEXT
                    // call would read the rotation as an account switch and throw away the
                    // snapshot this retry is about to record (and its happy-path cache).
                    if (_cachesSnapshots)
                    {
                        TrackCredentialFingerprint(freshCredential);
                    }
                    try
                    {
                        return await FetchWithPolicyAsync(freshCredential, cancellationToken);
                    }
                    catch (HttpRequestException retryEx) when (retryEx.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                    {
                        throw new AuthenticationRequiredException(Tool, retryEx.StatusCode!.Value);
                    }
                }
            }
            throw new AuthenticationRequiredException(Tool, ex.StatusCode!.Value);
        }
    }

    /// <summary>
    /// One fetch attempt with the policy's degradation applied: a retryable status arms
    /// the cooldown and serves the cache, any other failure serves the cache when the
    /// policy says so — while auth rejections pass through untouched for the caller's
    /// delegated-refresh recovery. Both the first attempt and the post-refresh retry go
    /// through here, so a 429 on the retry still arms the gate instead of escaping it
    /// (catch clauses of one try never re-match their siblings' throws).
    /// </summary>
    private async Task<UsageSnapshot> FetchWithPolicyAsync(ToolCredential credential, CancellationToken cancellationToken)
    {
        try
        {
            return RecordSuccess(await FetchSnapshotAsync(credential, cancellationToken));
        }
        catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            // Auth recovery (delegated refresh + retry) is owned by the caller; rethrow
            // before the serve-cached catch below can swallow a rejected token.
            throw;
        }
        catch (HttpRequestException ex) when (_gate is not null && RateLimitGate.IsRetryable(ex.StatusCode))
        {
            var cooldown = _gate.RecordFailure((ex as UsageEndpointException)?.RetryAfter);
            // Keep showing the last good value if we have one; only surface a failure on
            // a cold start with nothing cached. Logged only on the serve-cached branch:
            // it returns success downstream, so a throttled provider would otherwise
            // leave no trace, while the propagating one is recorded by UsageService.
            if (_lastSnapshot is not null)
            {
                DiagnosticsLog.Write(
                    "provider",
                    $"{ToolName} throttled ({(int)ex.StatusCode!.Value} x{_gate.ConsecutiveFailures}), serving cached; backing off {cooldown.TotalMinutes.ToString("0.#", CultureInfo.InvariantCulture)}m");
                return WithRefreshedPlan(_lastSnapshot, credential);
            }
            throw;
        }
        catch (Exception ex) when (_policy.ServeCachedOnAnyFailure && ex is not OperationCanceledException
            && _lastSnapshot is not null)
        {
            // Serving the cache turns this into a success downstream, so this is the only
            // place the failure can be recorded. A propagating one is left to
            // UsageService, which logs everything that reaches it.
            DiagnosticsLog.Write(
                "provider",
                $"{ToolName} fetch failed, serving cached: {ex.GetType().Name}: {ex.Message}");
            return WithRefreshedPlan(_lastSnapshot, credential);
        }
    }

    /// <summary>The provider's endpoint call and parsing — the only per-tool part.
    /// Build the snapshot with <see cref="Snapshot"/> so its capture time is consistent.</summary>
    protected abstract Task<UsageSnapshot> FetchSnapshotAsync(ToolCredential credential, CancellationToken cancellationToken);

    /// <summary>Whether the credential can authenticate a call (Cursor also needs the user id).</summary>
    protected virtual bool HasUsableCredential(ToolCredential credential)
        => credential.AccessToken is { Length: > 0 };

    /// <summary>
    /// The plan knowable from the credential file alone, shown even without a usage
    /// response and refreshed on every cache serve. Null for tools whose plan only comes
    /// from the usage response (the cached snapshot's plan is then kept).
    /// </summary>
    protected virtual string? PlanFromCredential(ToolCredential? credential) => null;

    protected UsageSnapshot Snapshot(string? plan, IReadOnlyList<UsageWindow> windows) => new()
    {
        ToolName = ToolName,
        Plan = plan,
        Windows = windows,
        CapturedAt = Time.GetUtcNow(),
    };

    protected UsageSnapshot Empty(ToolCredential? credential)
        => Snapshot(PlanFromCredential(credential), Array.Empty<UsageWindow>());

    /// <summary>
    /// Success gate for usage responses. Unlike <c>EnsureSuccessStatusCode</c> the thrown
    /// exception keeps the response's Retry-After, which the shared cooldown honors.
    /// </summary>
    protected void EnsureUsageSuccess(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }
        throw new UsageEndpointException(response.StatusCode, RetryAfterOf(response));
    }

    private TimeSpan? RetryAfterOf(HttpResponseMessage response)
    {
        var retryAfter = response.Headers.RetryAfter;
        if (retryAfter is null)
        {
            return null;
        }
        if (retryAfter.Delta is { } delta)
        {
            return delta;
        }
        if (retryAfter.Date is { } date)
        {
            var delay = date - Time.GetUtcNow();
            return delay > TimeSpan.Zero ? delay : TimeSpan.Zero;
        }
        return null;
    }

    private UsageSnapshot RecordSuccess(UsageSnapshot snapshot)
    {
        _gate?.RecordSuccess();
        if (_cachesSnapshots)
        {
            _lastSuccessTimestamp = Time.GetTimestamp();
            _lastSnapshot = snapshot;
        }
        return snapshot;
    }

    private UsageSnapshot WithRefreshedPlan(UsageSnapshot cached, ToolCredential? credential)
        => cached with { Plan = PlanFromCredential(credential) ?? cached.Plan };

    /// <summary>
    /// A CLI re-login/account switch must not serve the prior account's cache or
    /// cooldown. Keeps only a one-way fingerprint, never the token itself.
    /// </summary>
    private void TrackCredentialFingerprint(ToolCredential? credential)
    {
        var fingerprint = credential?.AccessToken is { Length: > 0 } accessToken
            ? Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(accessToken)))
            : null;
        if (_credentialFingerprintInitialized && !StringComparer.Ordinal.Equals(_credentialFingerprint, fingerprint))
        {
            _lastSnapshot = null;
            _lastSuccessTimestamp = null;
            _gate?.Reset();
        }
        _credentialFingerprint = fingerprint;
        _credentialFingerprintInitialized = true;
    }
}
