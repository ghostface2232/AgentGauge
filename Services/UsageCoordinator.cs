using Gauge.Models;
using Microsoft.UI.Dispatching;

namespace Gauge.Services;

/// <summary>
/// Drives usage refreshes and owns the cache.
///
/// - A <see cref="PeriodicTimer"/> refreshes every 60s. Each cycle calls all
///   providers in parallel, isolated per provider (via <see cref="UsageService"/>),
///   so one tool's failure never blocks another's update.
/// - Opening the popover triggers <see cref="ForceRefreshAsync"/>, debounced: if a
///   refresh ran within the last 10s we skip the data source and just re-emit the
///   cached state. The periodic refresh counts toward the debounce too, so we never
///   hammer ccusage.
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

    public UsageCoordinator(UsageService usageService, DispatcherQueue? dispatcher = null)
    {
        _usageService = usageService;
        _dispatcher = dispatcher;
    }

    /// <summary>Starts the periodic refresh loop (does an immediate first refresh).</summary>
    public void Start()
    {
        _loopTask ??= Task.Run(() => RunLoopAsync(_cts.Token));
    }

    /// <summary>
    /// Requests an immediate refresh, e.g. when the popover opens. Debounced: if a
    /// refresh ran within the last 10s, the cached state is re-emitted instead.
    /// </summary>
    public async Task ForceRefreshAsync()
    {
        var lastStarted = Interlocked.Read(ref _lastRefreshStartedTick);
        if (lastStarted != 0 && Environment.TickCount64 - lastStarted < ForcedRefreshDebounce.TotalMilliseconds)
        {
            // Within the debounce window: show the cached value, don't re-fetch.
            EmitState();
            return;
        }

        try
        {
            await RefreshAsync(_cts.Token);
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
            await RefreshAsync(cancellationToken); // immediate first load
            using var timer = new PeriodicTimer(RefreshInterval);
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                await RefreshAsync(cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal on shutdown.
        }
    }

    private async Task RefreshAsync(CancellationToken cancellationToken)
    {
        // Serialize cycles: if a refresh is already running, don't start another;
        // re-emit the current state so callers still get a fresh notification.
        if (!await _refreshGate.WaitAsync(0, cancellationToken))
        {
            EmitState();
            return;
        }

        try
        {
            Interlocked.Exchange(ref _lastRefreshStartedTick, Environment.TickCount64);
            var results = await _usageService.GetAllSnapshotsAsync(cancellationToken);
            MergeIntoCache(results);
            EmitState();
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private void MergeIntoCache(IReadOnlyList<ProviderSnapshotResult> results)
    {
        lock (_cacheLock)
        {
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
