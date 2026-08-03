namespace Gauge.Services;

/// <summary>
/// Exponential backoff schedule for endpoints that rate-limit without a Retry-After
/// header (so the client must pick its own cooldowns): doubles from the base per
/// consecutive failure, capped at the maximum.
/// </summary>
public sealed class BackoffPolicy(TimeSpan baseCooldown, TimeSpan maxCooldown)
{
    /// <summary>Cooldown for the Nth consecutive failure (1-based): base×2^(n−1), capped.</summary>
    public TimeSpan ForAttempt(int consecutiveFailures)
    {
        var shift = Math.Clamp(consecutiveFailures - 1, 0, 62);
        var doubled = baseCooldown.Ticks << shift;
        // A large shift overflows into zero/negative ticks; treat that as "past the cap".
        var ticks = doubled <= 0 ? maxCooldown.Ticks : Math.Min(maxCooldown.Ticks, doubled);
        return TimeSpan.FromTicks(ticks);
    }
}
