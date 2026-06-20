using Gauge.Models;
using Microsoft.UI.Dispatching;

namespace Gauge.Services;

public enum RefreshReason
{
    Periodic,
    PopoverOpened,
    Manual,
    AuthenticationChanged,
    // The set of enabled tools changed (user added/removed a service). Refresh
    // immediately (not debounced) so cards appear/disappear right away.
    ToolsChanged,
}

/// <summary>
/// Drives usage refreshes and owns the cache.
///
/// - A <see cref="PeriodicTimer"/> refreshes every 60s. Each cycle calls all
///   providers in parallel, isolated per provider (via <see cref="UsageService"/>),
///   so one tool's failure never blocks another's update.
/// - Opening the popover or requesting a manual refresh calls <see cref="RefreshAsync"/>,
///   debounced: if a
///   refresh ran within the last 10s we skip the data source and just re-emit the
///   cached state. The periodic refresh counts toward the debounce too, so we never
///   over-poll the providers.
/// - The last successful snapshot per tool is cached; on failure the cached value is
///   kept and surfaced with its capture time.
///
/// Relationship to the popover toggle guard: that guard decides whether a tray click
/// opens or closes the popover; this debounce decides whether an open re-fetches.
/// They never conflict because a forced refresh is requested only when the popover
/// actually opens (PopoverWindow.Opened), not on every click.
/// </summary>
public sealed class UsageCoordinator : IDisposable
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ForcedRefreshDebounce = TimeSpan.FromSeconds(10);

    private readonly UsageService _usageService;
    private readonly DispatcherQueue? _dispatcher;
    private readonly IUsageCachePersistence? _persistence;

    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private readonly object _cacheLock = new();
    private readonly Dictionary<string, CachedUsage> _cache = new();
    private readonly List<string> _toolOrder = new();

    private long _lastRefreshStartedTick;
    private Task? _loopTask;
    private bool _disposed;

    /// <summary>Raised after each refresh with the current cached state (on the UI thread).</summary>
    public event EventHandler<UsageState>? Updated;
    public event EventHandler<ToolKind>? AuthenticationRequired;

    public UsageCoordinator(
        UsageService usageService,
        DispatcherQueue? dispatcher = null,
        IUsageCachePersistence? persistence = null)
    {
        _usageService = usageService;
        _dispatcher = dispatcher;
        _persistence = persistence;
        RehydrateFromDisk();
    }

    /// <summary>Starts the periodic refresh loop (does an immediate first refresh).</summary>
    public void Start()
    {
        // Surface the rehydrated last-known values immediately, before the first network
        // refresh completes, so a popover opened right after boot is never empty.
        EmitState();
        _loopTask ??= Task.Run(() => RunLoopAsync(_cts.Token));
    }

    /// <summary>
    /// Seeds the cache from the last persisted snapshots so cards show a last-known value
    /// on a cold start (before any successful fetch). The stored capture time is kept, so
    /// the UI shows the value's true age until a live refresh replaces it.
    /// </summary>
    private void RehydrateFromDisk()
    {
        if (_persistence is null)
        {
            return;
        }

        foreach (var snapshot in _persistence.Load())
        {
            if (_cache.ContainsKey(snapshot.ToolName))
            {
                continue;
            }
            _toolOrder.Add(snapshot.ToolName);
            _cache[snapshot.ToolName] = new CachedUsage
            {
                ToolName = snapshot.ToolName,
                Snapshot = snapshot,
                LastUpdatedAt = snapshot.CapturedAt,
                LastRefreshFailed = false,
            };
        }
    }

    private void PersistSuccessfulSnapshots()
    {
        if (_persistence is null)
        {
            return;
        }

        List<UsageSnapshot> snapshots;
        lock (_cacheLock)
        {
            snapshots = _cache.Values
                .Where(c => c.Snapshot is not null)
                .Select(c => c.Snapshot!)
                .ToList();
        }
        _persistence.Save(snapshots);
    }

    /// <summary>
    /// Requests an immediate refresh, e.g. when the popover opens. Debounced: if a
    /// refresh ran within the last 10s, the cached state is re-emitted instead.
    /// </summary>
    public async Task RefreshAsync(RefreshReason reason)
    {
        var isDebouncedRequest = reason is RefreshReason.PopoverOpened or RefreshReason.Manual;
        var lastStarted = Interlocked.Read(ref _lastRefreshStartedTick);
        if (isDebouncedRequest && lastStarted != 0
            && Environment.TickCount64 - lastStarted < ForcedRefreshDebounce.TotalMilliseconds)
        {
            // Within the debounce window: show the cached value, don't re-fetch.
            EmitState();
            return;
        }

        try
        {
            await RefreshCoreAsync(
                _cts.Token,
                waitForExisting: reason is RefreshReason.AuthenticationChanged or RefreshReason.ToolsChanged);
        }
        catch (OperationCanceledException)
        {
            // Shutting down; ignore.
        }
    }

    private async Task RunLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await RefreshCoreAsync(cancellationToken); // immediate first load
            using var timer = new PeriodicTimer(RefreshInterval);
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                await RefreshCoreAsync(cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal on shutdown.
        }
    }

    private async Task RefreshCoreAsync(CancellationToken cancellationToken, bool waitForExisting = false)
    {
        // Serialize cycles: if a refresh is already running, don't start another;
        // re-emit the current state so callers still get a fresh notification.
        var entered = waitForExisting
            ? await WaitForGateAsync(cancellationToken)
            : await _refreshGate.WaitAsync(0, cancellationToken);
        if (!entered)
        {
            EmitState();
            return;
        }

        try
        {
            Interlocked.Exchange(ref _lastRefreshStartedTick, Environment.TickCount64);
            var results = await _usageService.GetAllSnapshotsAsync(cancellationToken);
            MergeIntoCache(results);
            ReportAuthenticationFailures(results);
            EmitState();
            // Persist only when at least one tool refreshed successfully, so the on-disk
            // last-known value is never overwritten by an all-failed cycle.
            if (results.Any(r => r.Succeeded))
            {
                PersistSuccessfulSnapshots();
            }
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private async Task<bool> WaitForGateAsync(CancellationToken cancellationToken)
    {
        await _refreshGate.WaitAsync(cancellationToken);
        return true;
    }

    private void ReportAuthenticationFailures(IReadOnlyList<ProviderSnapshotResult> results)
    {
        foreach (var error in results.Select(r => r.Error).OfType<AuthenticationRequiredException>())
        {
            var handler = AuthenticationRequired;
            if (handler is null) continue;
            if (_dispatcher is not null) _dispatcher.TryEnqueue(() => handler(this, error.Tool));
            else handler(this, error.Tool);
        }
    }

    private void MergeIntoCache(IReadOnlyList<ProviderSnapshotResult> results)
    {
        lock (_cacheLock)
        {
            // Drop tools no longer reported. UsageService returns exactly one result per
            // ENABLED provider (success or failure), so a tool missing from the results
            // was disabled (removed from the registry) — purge its cached card and order
            // entry so it disappears from the UI. (Failed-but-enabled tools still appear
            // here as error results and are kept below.)
            var present = new HashSet<string>(results.Select(r => r.ToolName));
            _toolOrder.RemoveAll(name => !present.Contains(name));
            foreach (var stale in _cache.Keys.Where(name => !present.Contains(name)).ToList())
            {
                _cache.Remove(stale);
            }

            foreach (var result in results)
            {
                if (!_toolOrder.Contains(result.ToolName))
                {
                    _toolOrder.Add(result.ToolName);
                }

                _cache.TryGetValue(result.ToolName, out var previous);
                _cache[result.ToolName] = result.Succeeded
                    ? new CachedUsage
                    {
                        ToolName = result.ToolName,
                        Snapshot = result.Snapshot,
                        LastUpdatedAt = result.Snapshot!.CapturedAt,
                        LastRefreshFailed = false,
                    }
                    : new CachedUsage
                    {
                        // Keep the last successful value; mark the attempt as failed.
                        ToolName = result.ToolName,
                        Snapshot = previous?.Snapshot,
                        LastUpdatedAt = previous?.LastUpdatedAt,
                        LastRefreshFailed = true,
                    };
            }
        }
    }

    private void EmitState()
    {
        UsageState state;
        lock (_cacheLock)
        {
            var tools = _toolOrder
                .Where(_cache.ContainsKey)
                .Select(name => _cache[name])
                .ToList();

            DateTimeOffset? lastUpdated = tools
                .Where(t => t.LastUpdatedAt.HasValue)
                .Select(t => t.LastUpdatedAt!.Value)
                .DefaultIfEmpty()
                .Max();
            if (lastUpdated == default(DateTimeOffset))
            {
                lastUpdated = null;
            }

            state = new UsageState { Tools = tools, LastUpdatedAt = lastUpdated };
        }

        var handler = Updated;
        if (handler is null)
        {
            return;
        }

        if (_dispatcher is not null)
        {
            _dispatcher.TryEnqueue(() => handler(this, state));
        }
        else
        {
            handler(this, state);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;

        try
        {
            _cts.Cancel();
        }
        catch
        {
            // ignore
        }

        _cts.Dispose();
        _refreshGate.Dispose();
    }
}
