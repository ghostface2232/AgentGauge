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

    /// <summary>
    /// Display group this window belongs to, when a tool's windows fall into named groups —
    /// e.g. Antigravity's "Gemini" and "Claude/GPT" model families, each with its own 5-hour and
    /// weekly limit. The card shows windows that share a group together under this heading. Null
    /// when the tool's windows are not grouped. Language-neutral (a product family name), so
    /// unlike <see cref="Label"/> it is persisted as-is.
    /// </summary>
    public string? GroupLabel { get; init; }

    /// <summary>Usage as a fraction in [0, 1].</summary>
    public required double UsedRatio { get; init; }

    /// <summary>Short label for display (e.g. "5시간", "주간").</summary>
    public required string Label { get; init; }

    /// <summary>
    /// Localization key behind <see cref="Label"/>, for the windows whose label is NOT
    /// simply derived from <see cref="Type"/>. GitHub Copilot's chat / completions / premium
    /// quotas are three <see cref="UsageWindowType.BillingCycle"/> windows that would
    /// otherwise all read "Usage" — indistinguishable both as rows and as toast titles.
    ///
    /// The key, not the resolved text, is what gets persisted, so a rehydrated cache
    /// re-resolves in whatever language is active now. An unrecognized key falls through
    /// <c>Loc.Get</c> to itself, so a provider id for a quota Gauge doesn't know yet still
    /// survives as its own label. Null when <see cref="Type"/> alone determines the label.
    /// </summary>
    public string? LabelKey { get; init; }

    /// <summary>When this window resets, if known.</summary>
    public DateTimeOffset? ResetTime { get; init; }

    /// <summary>
    /// Provider-reported duration of this limit window, when known. Keeping the duration
    /// alongside the reset time lets the UI compare utilization with elapsed time and lets
    /// providers classify windows by their actual contract instead of positional names such
    /// as "primary" and "secondary".
    /// </summary>
    public TimeSpan? Duration { get; init; }

    /// <summary>Raw tokens used in the window, if available (for display).</summary>
    public long? UsedTokens { get; init; }

    /// <summary>The denominator used to compute <see cref="UsedRatio"/>, if available.</summary>
    public long? LimitTokens { get; init; }
}
