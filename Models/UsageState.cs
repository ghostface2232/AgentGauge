namespace Gauge.Models;

/// <summary>
/// The full cached usage state emitted by the coordinator after each refresh.
/// Carries one <see cref="CachedUsage"/> per tool plus the most recent successful
/// update time across all tools (for a single "last updated" label).
/// </summary>
public sealed record UsageState
{
    public required IReadOnlyList<CachedUsage> Tools { get; init; }

    /// <summary>Most recent successful update across all tools, if any.</summary>
    public DateTimeOffset? LastUpdatedAt { get; init; }

    public static UsageState Empty { get; } = new()
    {
        Tools = Array.Empty<CachedUsage>(),
        LastUpdatedAt = null,
    };
}
