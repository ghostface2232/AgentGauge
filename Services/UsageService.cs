using Gauge.Models;
using Gauge.Providers;

namespace Gauge.Services;

internal sealed class UsageRefreshSkippedException : Exception;

/// <summary>Outcome of one provider's snapshot attempt.</summary>
public sealed record ProviderSnapshotResult
{
    public required ToolKind Tool { get; init; }

    public required string ToolName { get; init; }

    /// <summary>The snapshot, if the provider succeeded.</summary>
    public UsageSnapshot? Snapshot { get; init; }

    /// <summary>The failure, if the provider threw.</summary>
    public Exception? Error { get; init; }

    internal bool Skipped { get; init; }

    public bool Succeeded => Error is null && Snapshot is not null;
}

/// <summary>
/// Runs all providers and returns their results. Each provider call is isolated in
/// its own try/catch, so one provider's exception can never block another's result
/// or the UI update (AGENTS.md failure isolation). Providers run in parallel.
/// </summary>
public sealed class UsageService
{
    /// <summary>
    /// Upper bound on one provider's read. Every result waits on the slowest provider and the
    /// coordinator's single gate is held meanwhile, so one read that never ends would stop every
    /// tool's refresh until a restart. Well above the longest legitimate read: a delegated
    /// <c>codex doctor</c> (30s) followed by a fetch and its retry (20s each).
    /// </summary>
    internal static readonly TimeSpan DefaultReadDeadline = TimeSpan.FromSeconds(90);

    private readonly IReadOnlyList<IUsageProvider> _providers;
    private volatile IReadOnlyList<IUsageProvider> _additionalProviders = [];
    private readonly Func<ToolKind, bool> _isEnabled;
    private readonly Func<ToolKind, bool> _isActive;
    private readonly Func<string, bool> _isRegisteredAccount;
    private readonly TimeSpan _readDeadline;

    // Reads the deadline gave up on but that are still running. A provider gets no new call until
    // its read has actually ended: UsageProviderBase's cache and cooldown assume one call at a
    // time, and a call that stays blocked would otherwise cost another pool thread every cycle.
    private readonly Dictionary<IUsageProvider, Task> _abandonedReads = new();

    /// <param name="isEnabled">
    /// Only providers whose tool is enabled are queried. Defaults to "all enabled" so
    /// existing callers/tests are unaffected. The registry supplies this at runtime so
    /// disabled tools are neither fetched nor surfaced.
    /// </param>
    public UsageService(
        IEnumerable<IUsageProvider> providers,
        Func<ToolKind, bool>? isEnabled = null,
        Func<ToolKind, bool>? isActive = null,
        TimeSpan? readDeadline = null,
        Func<string, bool>? isRegisteredAccount = null)
    {
        _providers = providers.ToList();
        _isEnabled = isEnabled ?? (_ => true);
        _isActive = isActive ?? _isEnabled;
        _readDeadline = readDeadline ?? DefaultReadDeadline;
        _isRegisteredAccount = isRegisteredAccount ?? (_ => true);
    }

    /// <summary>The currently enabled providers, in catalog/registration order.</summary>
    public IReadOnlyList<IUsageProvider> GetEnabledProviders()
        => RegisteredProviders().Where(provider => _isActive(provider.Tool)
            && provider is not AccountUsageProvider { IsSigningIn: true }).ToList();

    internal void SetAdditionalProviders(IEnumerable<IUsageProvider> providers) =>
        _additionalProviders = providers.ToArray();

    public IReadOnlySet<string> GetRegisteredToolNames() =>
        RegisteredProviders().Select(p => p.ToolName).ToHashSet();

    private IEnumerable<IUsageProvider> RegisteredProviders() =>
        _providers.Concat(_additionalProviders).Where(p => _isEnabled(p.Tool) && _isRegisteredAccount(p.ToolName));

    public async Task<IReadOnlyList<ProviderSnapshotResult>> GetAllSnapshotsAsync(CancellationToken cancellationToken)
        => await GetSnapshotsAsync(GetEnabledProviders(), cancellationToken);

    /// <summary>
    /// Queries the selected providers in parallel. The coordinator uses this for periodic
    /// provider-specific cadences while forced refreshes still call every enabled provider.
    /// The interaction tells throttled providers whether a rate-limit cooldown may hold
    /// this fetch back (background) or must be bypassed (user-initiated).
    /// </summary>
    public async Task<IReadOnlyList<ProviderSnapshotResult>> GetSnapshotsAsync(
        IEnumerable<IUsageProvider> providers,
        CancellationToken cancellationToken,
        FetchInteraction interaction = FetchInteraction.Background)
    {
        var tasks = providers.Select(provider => GetIsolatedAsync(provider, interaction, cancellationToken));
        return await Task.WhenAll(tasks);
    }

    private async Task<ProviderSnapshotResult> GetIsolatedAsync(
        IUsageProvider provider, FetchInteraction interaction, CancellationToken cancellationToken)
    {
        if (IsStillRunningAbandonedRead(provider))
        {
            // Not logged again: the timeout that abandoned the read already was.
            return new ProviderSnapshotResult
            {
                Tool = provider.Tool,
                ToolName = provider.ToolName,
                Error = new TimeoutException($"{provider.ToolName} is still running a read that passed its deadline."),
            };
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task<UsageSnapshot>? read = null;
        try
        {
            // Task.Run so a provider blocking before its first await (a WMI query, a process
            // start) can't hold up the others, and so the wait below can still give up on it.
            var readToken = deadline.Token;
            read = Task.Run(() => provider.GetSnapshotAsync(interaction, readToken), readToken);
            // A provider stuck in a blocking call ignores its token, so the wait has to be able
            // to walk away. Anything it later throws must not surface as an unobserved exception.
            _ = read.ContinueWith(
                static task => _ = task.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            var snapshot = await read.WaitAsync(_readDeadline, cancellationToken);
            return new ProviderSnapshotResult { Tool = provider.Tool, ToolName = provider.ToolName, Snapshot = snapshot };
        }
        catch (UsageRefreshSkippedException)
        {
            return new ProviderSnapshotResult { Tool = provider.Tool, ToolName = provider.ToolName, Skipped = true };
        }
        catch (Exception ex)
        {
            // Lets an abandoned read that does honour its token stop working.
            await deadline.CancelAsync();
            if (read is { IsCompleted: false })
            {
                lock (_abandonedReads)
                {
                    _abandonedReads[provider] = read;
                }
            }

            // Auth-required and app shutdown are expected flows, not diagnostics events. A
            // cancellation nobody asked for is a timeout (HttpClient reports its own that way)
            // and is logged. Type + message only — never tokens, credential URLs, or CLI output.
            var isShutdown = ex is OperationCanceledException && cancellationToken.IsCancellationRequested;
            if (ex is not AuthenticationRequiredException && !isShutdown)
            {
                var toolName = provider is AccountUsageProvider ? ToolCatalog.For(provider.Tool).DisplayName : provider.ToolName;
                DiagnosticsLog.Write("provider", $"{toolName} refresh failed: {ex.GetType().Name}: {ex.Message}");
            }
            return new ProviderSnapshotResult { Tool = provider.Tool, ToolName = provider.ToolName, Error = ex };
        }
    }

    private bool IsStillRunningAbandonedRead(IUsageProvider provider)
    {
        lock (_abandonedReads)
        {
            if (!_abandonedReads.TryGetValue(provider, out var read))
            {
                return false;
            }
            if (!read.IsCompleted)
            {
                return true;
            }
            _abandonedReads.Remove(provider);
            return false;
        }
    }
}
