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
/// - A one-minute scheduler tick refreshes only providers whose own cadence is due:
///   Codex every 3 minutes, Claude/Cursor every 5, Antigravity every 10, and Copilot
///   every 15. Due providers still run in parallel and remain failure-isolated.
/// - Opening the popover or requesting a manual refresh calls <see cref="RefreshAsync"/>,
///   debounced: if a
///   refresh ran within the last 10s we skip the data source and just re-emit the
///   cached state. The periodic refresh counts toward the debounce too, so we never
///   over-poll the providers.
/// - Beyond that debounce, opening the popover also respects a per-provider cost floor
///   (<see cref="ForcedIntervalFor"/>), which exists for providers whose read is
///   expensive in a way a 10-second gap does not cover. Explicit user actions (the
///   refresh button, a sign-in, adding a tool) bypass it.
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
    private static readonly TimeSpan SchedulerTickInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan ForcedRefreshDebounce = TimeSpan.FromSeconds(10);

    // Sentinel for "no refresh has been attempted yet". Not 0: a timestamp source is free to
    // start there, which would make the very first refresh read as an old one.
    private const long NeverRefreshed = long.MinValue;

    private readonly UsageService _usageService;
    private readonly DispatcherQueue? _dispatcher;
    private readonly IUsageCachePersistence? _persistence;
    private readonly IUsageHistoryRecorder? _history;
    private readonly TimeProvider _time;

    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private readonly object _cacheLock = new();
    private readonly Dictionary<string, CachedUsage> _cache = new();
    private readonly List<string> _toolOrder = new();
    // Monotonic timestamps, so the intervals below survive a wall-clock change.
    private readonly Dictionary<ToolKind, long> _lastProviderAttemptTimestamps = new();

    private long _lastRefreshStartedTimestamp = NeverRefreshed;
    private Task? _loopTask;
    // Volatile: read by refresh entry points that may resume off the UI thread (and by
    // tests exercising them cross-thread); the early-return must see Dispose's write
    // without a lock.
    private volatile bool _disposed;

    /// <summary>Raised after each refresh with the current cached state (on the UI thread).</summary>
    public event EventHandler<UsageState>? Updated;
    public event EventHandler<ToolKind>? AuthenticationRequired;

    /// <summary>
    /// Raised when a tool's fetch succeeds, so a prior server-side rejection (which is
    /// sticky per token fingerprint) can be cleared once the token is accepted again.
    /// </summary>
    public event EventHandler<ToolKind>? AuthenticationRecovered;

    public UsageCoordinator(
        UsageService usageService,
        DispatcherQueue? dispatcher = null,
        IUsageCachePersistence? persistence = null,
        IUsageHistoryRecorder? history = null,
        TimeProvider? time = null)
    {
        _usageService = usageService;
        _dispatcher = dispatcher;
        _persistence = persistence;
        _history = history;
        _time = time ?? TimeProvider.System;
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
        // A wake/unlock handler can race DisposePipeline and call in after teardown;
        // that must be a quiet no-op, not an ObjectDisposedException from _cts.
        if (_disposed)
        {
            return;
        }

        var isDebouncedRequest = reason is RefreshReason.PopoverOpened or RefreshReason.Manual;
        var lastStarted = Interlocked.Read(ref _lastRefreshStartedTimestamp);
        if (isDebouncedRequest && lastStarted != NeverRefreshed
            && _time.GetElapsedTime(lastStarted) < ForcedRefreshDebounce)
        {
            // Within the debounce window: show the cached value, don't re-fetch.
            EmitState();
            return;
        }

        await RefreshGuardedAsync(
            reason,
            waitForExisting: reason is RefreshReason.AuthenticationChanged or RefreshReason.ToolsChanged);
    }

    /// <summary>
    /// Runs one refresh cycle with every failure contained. Callers are async void UI
    /// handlers and the scheduler loop: an escaping exception would crash the process
    /// (or silently kill the loop), so cancellation and disposal are treated as normal
    /// shutdown races and anything unexpected is logged instead of rethrown.
    /// </summary>
    private async Task RefreshGuardedAsync(RefreshReason reason, bool waitForExisting = false)
    {
        try
        {
            // _cts.Token is read inside the try: Dispose can tear the CTS down between
            // the _disposed check and here, and that must land in the catch below.
            await RefreshCoreAsync(reason, _cts.Token, waitForExisting);
        }
        catch (OperationCanceledException)
        {
            // Shutting down; ignore.
        }
        catch (ObjectDisposedException) when (_disposed)
        {
            // Raced Dispose (the CTS or gate was torn down mid-call); shutting down.
            // The filter keeps a genuine mid-session ODE from persistence/history
            // falling through to the logging catch below instead of being masked.
        }
        catch (Exception ex)
        {
            DiagnosticsLog.Write("coordinator", $"Refresh({reason}) failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private async Task RunLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            // Guarded per cycle so one unexpected failure logs and the scheduler keeps
            // ticking, rather than dying silently for the rest of the session.
            await RefreshGuardedAsync(RefreshReason.Periodic); // immediate first load
            using var timer = new PeriodicTimer(SchedulerTickInterval);
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                await RefreshGuardedAsync(RefreshReason.Periodic);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal on shutdown.
        }
    }

    private async Task RefreshCoreAsync(
        RefreshReason reason,
        CancellationToken cancellationToken,
        bool waitForExisting = false)
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
            var enabledProviders = _usageService.GetEnabledProviders();
            var attemptTimestamp = _time.GetTimestamp();
            var providersToRefresh = reason switch
            {
                RefreshReason.Periodic => enabledProviders
                    .Where(provider => IsDue(provider.Tool, attemptTimestamp, PeriodicIntervalFor(provider.Tool)))
                    .ToList(),
                RefreshReason.PopoverOpened => enabledProviders
                    .Where(provider => IsDue(provider.Tool, attemptTimestamp, ForcedIntervalFor(provider.Tool)))
                    .ToList(),
                _ => enabledProviders,
            };

            if (providersToRefresh.Count == 0)
            {
                // A scheduler tick often has no due provider. It is not a refresh attempt and
                // must not affect the 10-second user-action debounce, and there is no new
                // state to announce.
                if (reason == RefreshReason.Periodic)
                {
                    return;
                }

                // Every other reason is a user action, so the cached state is still re-emitted
                // even when nothing was fetched — a popover opened while every provider is
                // inside its cost floor must show its cards, and removing the final enabled
                // tool must purge and persist the old card despite there being nothing to fetch.
                var removedAnyTool = reason == RefreshReason.ToolsChanged
                    && MergeIntoCache(
                        Array.Empty<ProviderSnapshotResult>(),
                        enabledProviders.Select(provider => provider.ToolName).ToHashSet());
                EmitState();
                if (removedAnyTool)
                {
                    PersistSuccessfulSnapshots();
                }
                return;
            }

            Interlocked.Exchange(ref _lastRefreshStartedTimestamp, attemptTimestamp);
            foreach (var provider in providersToRefresh)
            {
                _lastProviderAttemptTimestamps[provider.Tool] = attemptTimestamp;
            }

            // Snapshot the cached capture-times before merging so a genuine live fetch can
            // be told apart from a re-served cache (see ReportAuthenticationOutcomes).
            Dictionary<string, DateTimeOffset?> priorCaptured;
            lock (_cacheLock)
            {
                priorCaptured = _cache.ToDictionary(kv => kv.Key, kv => kv.Value.Snapshot?.CapturedAt);
            }
            var results = await _usageService.GetSnapshotsAsync(providersToRefresh, cancellationToken);
            var enabledToolNames = enabledProviders.Select(provider => provider.ToolName).ToHashSet();
            var purgedTools = MergeIntoCache(results, enabledToolNames);
            ReportAuthenticationOutcomes(results, priorCaptured);
            RecordHistory(results, priorCaptured);
            EmitState();
            // Persist when at least one tool refreshed successfully, so an all-failed cycle
            // never overwrites the on-disk last-known value. Also persist when a tool was
            // purged (disabled/removed), even with no success, so the removed tool doesn't
            // linger in the cache file and reappear on next launch via rehydration.
            if (purgedTools || results.Any(r => r.Succeeded))
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

    private bool IsDue(ToolKind tool, long nowTimestamp, TimeSpan interval)
        => interval <= TimeSpan.Zero
            || !_lastProviderAttemptTimestamps.TryGetValue(tool, out var previous)
            || _time.GetElapsedTime(previous, nowTimestamp) >= interval;

    /// <summary>
    /// Provider-specific background cadence. User-triggered and authentication/tool changes
    /// bypass these intervals and still refresh every enabled provider immediately.
    /// </summary>
    internal static TimeSpan PeriodicIntervalFor(ToolKind tool) => tool switch
    {
        ToolKind.Codex => TimeSpan.FromMinutes(3),
        ToolKind.ClaudeCode or ToolKind.Cursor => TimeSpan.FromMinutes(5),
        ToolKind.Antigravity => TimeSpan.FromMinutes(10),
        ToolKind.GitHubCopilot => TimeSpan.FromMinutes(15),
        _ => TimeSpan.FromMinutes(5),
    };

    /// <summary>
    /// Minimum spacing between attempts for a provider on a popover-open refresh. Unlike
    /// <see cref="PeriodicIntervalFor"/> this bounds COST, not staleness, so it is zero for
    /// everything whose read is one HTTP GET — those stay current on every open, and Claude
    /// additionally serves its own 5-minute network cache.
    ///
    /// Antigravity is the exception: with the IDE closed it has no endpoint to call, so a
    /// read launches a language server, waits for it to answer, and tears the process tree
    /// down again. Without a floor every tray click spawns one, which the 10-second debounce
    /// does not prevent. Ten minutes of drift is the periodic cadence anyway, so a two-minute
    /// floor costs no meaningful freshness; the refresh button bypasses the floor (though it
    /// still shares the 10-second debounce, so a click right after a popover-open can land
    /// inside it).
    /// </summary>
    internal static TimeSpan ForcedIntervalFor(ToolKind tool) => tool switch
    {
        ToolKind.Antigravity => TimeSpan.FromMinutes(2),
        _ => TimeSpan.Zero,
    };

    private void ReportAuthenticationOutcomes(
        IReadOnlyList<ProviderSnapshotResult> results, IReadOnlyDictionary<string, DateTimeOffset?> priorCaptured)
    {
        foreach (var result in results)
        {
            if (result.Error is AuthenticationRequiredException authError)
            {
                Raise(AuthenticationRequired, authError.Tool);
            }
            else if (IsLiveServerSuccess(result, priorCaptured))
            {
                // Only a genuine live fetch proves the server accepted the token; clear any
                // sticky rejection so a tool that recovered stops showing as signed out.
                Raise(AuthenticationRecovered, result.Tool);
            }
        }
    }

    /// <summary>
    /// True only when a result represents a fresh server-accepted fetch — a snapshot with
    /// real windows whose <see cref="UsageSnapshot.CapturedAt"/> advanced past the previously
    /// cached one. This deliberately excludes outcomes that are "successes" but do NOT prove
    /// the server accepted the token: an empty snapshot returned when there is no credential,
    /// and a provider re-serving its last good snapshot during a cooldown/429/network error
    /// (those reuse the prior <c>CapturedAt</c>). Without this gate, logging out or a network
    /// blip after a 401 would wrongly clear the rejection and flip the card back to signed-in.
    /// </summary>
    private static bool IsLiveServerSuccess(
        ProviderSnapshotResult result, IReadOnlyDictionary<string, DateTimeOffset?> priorCaptured)
    {
        if (!result.Succeeded || result.Snapshot is not { Windows.Count: > 0 } snapshot)
        {
            return false;
        }
        return !priorCaptured.TryGetValue(result.ToolName, out var prior)
            || prior is null
            || snapshot.CapturedAt > prior.Value;
    }

    /// <summary>
    /// Appends genuinely live readings to the history store. The same live-fetch gate as
    /// authentication recovery applies: a provider re-serving its cached snapshot during a
    /// cooldown/429 reuses the prior <c>CapturedAt</c> and must not produce history rows,
    /// or trend math would see a flat line of duplicates.
    /// </summary>
    private void RecordHistory(
        IReadOnlyList<ProviderSnapshotResult> results, IReadOnlyDictionary<string, DateTimeOffset?> priorCaptured)
    {
        if (_history is null)
        {
            return;
        }
        foreach (var result in results)
        {
            if (IsLiveServerSuccess(result, priorCaptured))
            {
                _history.Record(result.Snapshot!);
            }
        }
    }

    private void Raise(EventHandler<ToolKind>? handler, ToolKind tool)
    {
        if (handler is null) return;
        if (_dispatcher is not null) _dispatcher.TryEnqueue(() => handler(this, tool));
        else handler(this, tool);
    }

    /// <summary>Merges a refresh cycle's results into the cache; returns true if any stale tool was purged.</summary>
    private bool MergeIntoCache(
        IReadOnlyList<ProviderSnapshotResult> results,
        IReadOnlySet<string> enabledToolNames)
    {
        lock (_cacheLock)
        {
            // Periodic cycles may query only a subset of enabled providers, so absence from
            // this result batch does not mean removal. The registry-backed enabled set is
            // the authority for purging cards.
            var staleKeys = _cache.Keys.Where(name => !enabledToolNames.Contains(name)).ToList();
            _toolOrder.RemoveAll(name => !enabledToolNames.Contains(name));
            foreach (var stale in staleKeys)
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

            return staleKeys.Count > 0;
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

        // Let the loop observe the cancel and unwind. A loop cycle runs entirely on the
        // thread pool, so it exits in milliseconds; the bound only keeps a pathological
        // hang from stalling exit. A cycle started from a UI async void handler is
        // different: without ConfigureAwait(false) its continuations need the UI thread,
        // which Dispose is blocking, so such a cycle CANNOT finish here — its held gate
        // is what the leak branch below exists for.
        try
        {
            _loopTask?.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
            // The loop's own failure no longer matters during teardown.
        }

        // Dispose the gate and CTS only when provably idle (the gate could be taken).
        // A refresh still holding the gate would hit ObjectDisposedException in its
        // finally-Release and escape into the async void UI handlers; if the gate cannot
        // be acquired promptly, leak both instead — the process is exiting and neither
        // holds state that outlives it, while disposing under a holder turns a clean
        // shutdown into a crash.
        if (_refreshGate.Wait(TimeSpan.FromMilliseconds(500)))
        {
            _refreshGate.Dispose();
            _cts.Dispose();
        }
    }
}
