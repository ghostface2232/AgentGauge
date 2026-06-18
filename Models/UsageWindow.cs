namespace Gauge.Models;

/// <summary>
/// One usage window for a tool (e.g. its 5-hour session or weekly quota).
///
/// <see cref="UsedRatio"/> is normalized 0–1. Because ccusage does not report a
/// tool's actual quota, providers estimate the ratio (see provider comments); this
/// is accepted for v1 and may be replaced by official figures (OAuth) in v1.5.
/// </summary>
public sealed record UsageWindow
{
    /// <summary>Which window this represents (5-hour, weekly, …).</summary>
    public required UsageWindowType Type { get; init; }

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
