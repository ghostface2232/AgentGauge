namespace Gauge.Models;

/// <summary>
/// Token counts for one or more model responses, normalized so every provider means the
/// same thing by each field: <see cref="Input"/> is the uncached prompt share only, with
/// <see cref="CacheRead"/> and <see cref="CacheWrite"/> the cached and newly cached shares
/// beside it (Codex reports the cached shares as subsets of its input count; Claude Code
/// reports them separately — both land here in the Claude shape).
/// <see cref="CacheWrite1h"/> is Claude's one-hour cache slice, billed at its own rate.
/// </summary>
public readonly record struct TokenTotals(
    long Input, long CacheRead, long CacheWrite, long CacheWrite1h, long Output)
{
    public static readonly TokenTotals Zero = default;

    public long Total => Input + CacheRead + CacheWrite + CacheWrite1h + Output;

    /// <summary>The whole prompt as the provider saw it — what a long-context price tier keys on.</summary>
    public long PromptTotal => Input + CacheRead + CacheWrite + CacheWrite1h;

    public bool IsZero => Total == 0;

    public static TokenTotals operator +(TokenTotals a, TokenTotals b) => new(
        a.Input + b.Input, a.CacheRead + b.CacheRead, a.CacheWrite + b.CacheWrite,
        a.CacheWrite1h + b.CacheWrite1h, a.Output + b.Output);
}

/// <summary>
/// One model response as read from a CLI's local session log: when it happened, which
/// model answered, what it consumed, and the identity that de-duplicates it against the
/// same response written again (Claude Code rewrites a streamed message several times;
/// Codex may re-emit a count).
/// </summary>
public sealed record UsageLogRecord(DateTimeOffset Timestamp, string Model, TokenTotals Tokens, string? Key);

/// <summary>
/// What one tool's local logs add up to for a calendar month, priced at public API list
/// rates. It is an estimate of API-equivalent spend, never a bill: a subscriber pays a flat
/// fee, and models without a known rate contribute tokens but no dollars, so
/// <see cref="CostUsd"/> is a floor whenever <see cref="UnpricedTokens"/> is positive.
/// </summary>
public sealed record ApiCostEstimate(
    string ToolName,
    int Year,
    int Month,
    decimal CostUsd,
    TokenTotals PricedTokens,
    long UnpricedTokens,
    IReadOnlyList<string> UnpricedModels,
    DateTimeOffset ScannedAt)
{
    public bool HasUnpriced => UnpricedTokens > 0;
    public bool IsEmpty => PricedTokens.IsZero && UnpricedTokens == 0;
}
