namespace Gauge.Models;

/// <summary>
/// One usage window for a tool (e.g. its 5-hour session or weekly quota).
///
/// <see cref="UsedRatio"/> is the real utilization (0–1) reported by the provider's
/// official usage API — Anthropic's OAuth usage endpoint for Claude Code and the
/// ChatGPT backend usage endpoint for Codex — not an estimate. <see cref="ResetTime"/>
/// is the provider's actual rate-limit reset, not a calendar boundary.
/// </summary>
public sealed record UsageWindow
{
    /// <summary>Which window this represents (5-hour, weekly, …).</summary>
    public required UsageWindowType Type { get; init; }

    /// <summary>
    /// Provider-stable identity for this window within its tool's snapshot, independent of
    /// <see cref="Type"/> and the language-dependent <see cref="Label"/>. A tool may expose
    /// two windows of the same <see cref="Type"/> (e.g. Antigravity's Gemini and Claude/GPT
    /// 5-hour limits); their Ids keep them distinct for reconciliation, notification
    /// baselines, and cache persistence. Null for providers with at most one window per type.
    /// </summary>
    public string? Id { get; init; }

    /// <summary>
    /// Identity used to match this window across refreshes: <see cref="Id"/> when set, else
    /// the <see cref="Type"/>. Two windows within one snapshot must have distinct keys.
    /// </summary>
    public string Key => Id ?? Type.ToString();

    /// <summary>Usage as a fraction in [0, 1].</summary>
    public required double UsedRatio { get; init; }

    /// <summary>Short label for display (e.g. "5시간", "주간").</summary>
    public required string Label { get; init; }

    /// <summary>When this window resets, if known.</summary>
    public DateTimeOffset? ResetTime { get; init; }

    /// <summary>Raw tokens used in the window, if available (for display).</summary>
    public long? UsedTokens { get; init; }

    /// <summary>The denominator used to compute <see cref="UsedRatio"/>, if available.</summary>
    public long? LimitTokens { get; init; }
}
