namespace Gauge.Models;

public enum UsageNotificationKind
{
    Threshold,
    Reset,
}

/// <summary>A UI-agnostic notification derived from normalized usage snapshots.</summary>
public sealed record UsageNotification
{
    public required UsageNotificationKind Kind { get; init; }
    public required UsageLevel Level { get; init; }
    public required string ToolName { get; init; }
    public required UsageWindowType WindowType { get; init; }
    public required string Title { get; init; }
    public required string Message { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
}
