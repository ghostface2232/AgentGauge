namespace Gauge.Models;

/// <summary>
/// One tool account's usage at one point in time. This is the single shared model the UI
/// and view models depend on — never on a provider's implementation or source
/// specifics — so the data source can be swapped without touching the UI.
/// </summary>
public sealed record UsageSnapshot
{
    /// <summary>Storage identity. The default account uses the tool's display name for compatibility.</summary>
    public required string ToolName { get; init; }
    // A one-way hash of a known account identifier, independent of token rotation and history.
    internal string? AccountIdentity { get; init; }
    // HTTP providers report whether the server accepted this read. Null lets other providers
    // use the capture timestamp to distinguish live data from their own cache.
    internal bool? IsLive { get; init; }
    internal bool IsSignedOut { get; init; }
    internal string? HistoryIdentity { get; init; }
    // Only live Copilot reads with a known login can adopt their old token partition.
    internal string? LegacyCopilotTokenIdentity { get; init; }
    internal string HistoryKey => HistoryIdentity is null ? ToolName : $"{ToolName}#{HistoryIdentity}";

    /// <summary>
    /// Subscription/plan label to show beside the tool name (e.g. "Max 5x", "Plus").
    /// Null when unknown. Comes from the provider's credentials/usage response, so it
    /// can be present even when no window data was obtained.
    /// </summary>
    public string? Plan { get; init; }

    /// <summary>The provider attempted to confirm the plan but could not obtain a label.</summary>
    public bool PlanUnavailable { get; init; }

    public string? SourceLabel { get; init; }

    /// <summary>
    /// The windows this tool exposes. A tool may have a 5-hour window, a weekly
    /// window, scoped model limits, any subset of those, or none — the list reflects
    /// only what was actually obtained.
    /// </summary>
    public required IReadOnlyList<UsageWindow> Windows { get; init; }

    /// <summary>
    /// How many manual rate-limit resets the account still holds, for tools that sell or
    /// grant them (Codex). This is a count of resets, not a utilization, so it belongs on
    /// the snapshot rather than in <see cref="Windows"/> — it has no ratio, no reset time,
    /// and no window to attach to. Null for every tool that exposes no such allowance.
    /// </summary>
    public int? ResetCredits { get; init; }

    /// <summary>Reported AI credits consumed, with no implied allowance.</summary>
    public double? AiCreditsUsed { get; init; }

    public bool HasUsage => Windows.Count > 0 || AiCreditsUsed is not null;

    /// <summary>Expiry times for available manual resets, separate from scheduled usage rollovers.</summary>
    public IReadOnlyList<ResetCreditDeadline>? ResetCreditDeadlines { get; init; }

    /// <summary>When this snapshot was captured.</summary>
    public DateTimeOffset CapturedAt { get; init; }
}
