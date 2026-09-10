namespace Gauge.Models;

/// <summary>
/// One tool's usage at one point in time. This is the single shared model the UI
/// and view models depend on — never on a provider's implementation or source
/// specifics — so the data source can be swapped without touching the UI.
/// </summary>
public sealed record UsageSnapshot
{
    /// <summary>Display name of the tool (e.g. "Claude Code", "Codex").</summary>
    public required string ToolName { get; init; }

    /// <summary>
    /// Subscription/plan label to show beside the tool name (e.g. "Max 5x", "Plus").
    /// Null when unknown. Comes from the provider's credentials/usage response, so it
    /// can be present even when no window data was obtained.
    /// </summary>
    public string? Plan { get; init; }

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

    /// <summary>When this snapshot was captured.</summary>
    public DateTimeOffset CapturedAt { get; init; }
}
