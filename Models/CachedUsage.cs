namespace Gauge.Models;

/// <summary>
/// The coordinator's cached view of one tool: its last successful snapshot plus
/// freshness metadata. On a failed refresh the snapshot is retained and
/// <see cref="LastRefreshFailed"/> is set, so the UI can keep showing the last good
/// value alongside the time it was captured.
/// </summary>
public sealed record CachedUsage
{
    public required string ToolName { get; init; }

    /// <summary>Last successful snapshot, or null if the tool has never succeeded.</summary>
    public UsageSnapshot? Snapshot { get; init; }

    /// <summary>When <see cref="Snapshot"/> was captured.</summary>
    public DateTimeOffset? LastUpdatedAt { get; init; }

    /// <summary>True if the most recent refresh attempt for this tool failed.</summary>
    public bool LastRefreshFailed { get; init; }

    /// <summary>True when there is a snapshot to show (possibly stale).</summary>
    public bool HasData => Snapshot is not null;
}
