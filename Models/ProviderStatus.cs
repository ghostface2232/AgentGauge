namespace Gauge.Models;

public enum ProviderStatusLevel { Unknown, Operational, Minor, Major, Critical, Maintenance }

/// <summary>Public service health, independent of account credentials and usage snapshots.</summary>
public sealed record ProviderStatus(
    ToolKind Tool, ProviderStatusLevel Level, string Summary,
    DateTimeOffset? CheckedAt, bool LastCheckFailed = false);
