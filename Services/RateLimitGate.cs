using System.Net;
using Gauge.Providers;

namespace Gauge.Services;

/// <summary>
/// Shared rate-limit cooldown for HTTP usage endpoints, modeled on CodexBar's
/// rate-limit gate: when an endpoint answers with a retryable status, background
/// fetches are held back for a cooldown so Gauge never hammers a throttling server,
/// while a user-initiated refresh is never blocked. The cooldown honors the server's
/// <c>Retry-After</c> (clamped, so a bogus header cannot freeze refreshes for hours)
/// and falls back to the <see cref="BackoffPolicy"/> escalation when the header is
/// absent (Claude's endpoint sends none). A new failure only ever extends the block —
/// it never shortens a longer cooldown already in force.
///
/// Timing uses the injected <see cref="TimeProvider"/>'s monotonic timestamps, so a
/// wall-clock change can neither stall nor skip a cooldown.
/// </summary>
public sealed class RateLimitGate(BackoffPolicy backoff, TimeProvider time)
{
    /// <summary>
    /// Longest cooldown a server-sent Retry-After may impose. Anything above it is a
    /// misbehaving header, not a real schedule.
    /// </summary>
    private static readonly TimeSpan MaxRetryAfter = TimeSpan.FromHours(1);

    private long? _cooldownStartedTimestamp;
    private TimeSpan _cooldownDuration;
    private int _consecutiveFailures;

    /// <summary>Retryable failures since the last success (for the diagnostics line).</summary>
    public int ConsecutiveFailures => _consecutiveFailures;

    /// <summary>
    /// Statuses worth retrying later: the request was fine, the server just couldn't
    /// serve it right now. Auth failures (401/403) are deliberately excluded — they
    /// have their own delegated-refresh path, and waiting would not fix them.
    /// </summary>
    public static bool IsRetryable(HttpStatusCode? status) => status is
        HttpStatusCode.RequestTimeout
        or HttpStatusCode.TooManyRequests
        or HttpStatusCode.InternalServerError
        or HttpStatusCode.BadGateway
        or HttpStatusCode.ServiceUnavailable
        or HttpStatusCode.GatewayTimeout;

    /// <summary>Whether a fetch with this interaction should be held back right now.</summary>
    public bool ShouldBlock(FetchInteraction interaction, long nowTimestamp)
        => interaction == FetchInteraction.Background && InCooldown(nowTimestamp);

    public bool InCooldown(long nowTimestamp)
        => _cooldownStartedTimestamp is { } started
            && time.GetElapsedTime(started, nowTimestamp) < _cooldownDuration;

    /// <summary>
    /// Records one retryable failure and returns the cooldown now in force. The server's
    /// Retry-After wins when it supplies one (clamped to <see cref="MaxRetryAfter"/>);
    /// otherwise the backoff schedule escalates per consecutive failure.
    /// </summary>
    public TimeSpan RecordFailure(TimeSpan? retryAfter)
    {
        _consecutiveFailures++;
        var candidate = retryAfter is { } after && after > TimeSpan.Zero
            ? (after < MaxRetryAfter ? after : MaxRetryAfter)
            : backoff.ForAttempt(_consecutiveFailures);

        // Extend-only: a short Retry-After on a later failure must not cut a longer
        // block already standing (CodexBar's max(existing, candidate) semantics).
        var nowTimestamp = time.GetTimestamp();
        var remaining = _cooldownStartedTimestamp is { } started
            ? _cooldownDuration - time.GetElapsedTime(started, nowTimestamp)
            : TimeSpan.Zero;
        _cooldownStartedTimestamp = nowTimestamp;
        _cooldownDuration = candidate > remaining ? candidate : remaining;
        return _cooldownDuration;
    }

    /// <summary>A successful fetch clears the block and the escalation.</summary>
    public void RecordSuccess()
    {
        _consecutiveFailures = 0;
        _cooldownStartedTimestamp = null;
        _cooldownDuration = default;
    }

    /// <summary>Clears all state, e.g. when the account/token behind the endpoint changed.</summary>
    public void Reset() => RecordSuccess();
}
