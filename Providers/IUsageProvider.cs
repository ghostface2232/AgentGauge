using Gauge.Models;

namespace Gauge.Providers;

/// <summary>
/// A source of usage data for one tool. Every provider normalizes its results into
/// the shared <see cref="UsageSnapshot"/> model, so the UI never depends on how the
/// data was obtained.
/// </summary>
public interface IUsageProvider
{
    /// <summary>Display name of the tool this provider reports on.</summary>
    string ToolName { get; }

    /// <summary>
    /// Collects a current snapshot. Implementations should degrade gracefully
    /// (omit windows that cannot be obtained) rather than throwing for ordinary
    /// conditions like "ccusage missing" or "tool never used".
    /// </summary>
    Task<UsageSnapshot> GetSnapshotAsync(CancellationToken cancellationToken);
}
