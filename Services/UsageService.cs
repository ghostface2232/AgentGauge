using Gauge.Models;
using Gauge.Providers;

namespace Gauge.Services;

/// <summary>Outcome of one provider's snapshot attempt.</summary>
public sealed record ProviderSnapshotResult
{
    public required ToolKind Tool { get; init; }

    public required string ToolName { get; init; }

    /// <summary>The snapshot, if the provider succeeded.</summary>
    public UsageSnapshot? Snapshot { get; init; }

    /// <summary>The failure, if the provider threw.</summary>
    public Exception? Error { get; init; }

    public bool Succeeded => Error is null && Snapshot is not null;
}

/// <summary>
/// Runs all providers and returns their results. Each provider call is isolated in
/// its own try/catch, so one provider's exception can never block another's result
/// or the UI update (AGENTS.md failure isolation). Providers run in parallel.
/// </summary>
public sealed class UsageService
{
    private readonly IReadOnlyList<IUsageProvider> _providers;
    private readonly Func<ToolKind, bool> _isEnabled;

    /// <param name="isEnabled">
    /// Only providers whose tool is enabled are queried. Defaults to "all enabled" so
    /// existing callers/tests are unaffected. The registry supplies this at runtime so
    /// disabled tools are neither fetched nor surfaced.
    /// </param>
    public UsageService(IEnumerable<IUsageProvider> providers, Func<ToolKind, bool>? isEnabled = null)
    {
        _providers = providers.ToList();
        _isEnabled = isEnabled ?? (_ => true);
    }

    public async Task<IReadOnlyList<ProviderSnapshotResult>> GetAllSnapshotsAsync(CancellationToken cancellationToken)
    {
        var tasks = _providers
            .Where(provider => _isEnabled(provider.Tool))
            .Select(provider => GetIsolatedAsync(provider, cancellationToken));
        return await Task.WhenAll(tasks);
    }

    private static async Task<ProviderSnapshotResult> GetIsolatedAsync(IUsageProvider provider, CancellationToken cancellationToken)
    {
        try
        {
            var snapshot = await provider.GetSnapshotAsync(cancellationToken);
            return new ProviderSnapshotResult { Tool = provider.Tool, ToolName = provider.ToolName, Snapshot = snapshot };
        }
        catch (Exception ex)
        {
            return new ProviderSnapshotResult { Tool = provider.Tool, ToolName = provider.ToolName, Error = ex };
        }
    }
}
