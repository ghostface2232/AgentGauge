using Gauge.Models;
using Gauge.Providers;
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
    // Wake-from-sleep / session-unlock. Refreshes every provider like Manual (and
    // shares the popover's 10s debounce so a resume-then-unlock pair collapses), but it is NOT a
    // user's explicit ask for this data, so it must not pierce a rate-limit cooldown —
    // a lock/unlock-heavy day would otherwise keep poking a throttling endpoint.
    SystemResumed,
}

/// <summary>
/// Drives usage refreshes and owns the cache.
///
/// - A one-minute scheduler tick refreshes only providers whose own cadence is due:
///   Codex every 3 minutes, Claude every 5, Antigravity every 10, and Copilot
///   every 15. Due providers still run in parallel and remain failure-isolated.
/// - Opening the popover or resuming the system waits for an active cycle and skips
///   only providers that finished a read within 10s of the request or while it waited.
///   The refresh button has its own 10s debounce and also waits for an active cycle.
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
    private readonly Func<AdaptiveRefreshSignals> _readAdaptiveSignals;
    private long _lastPopoverOpenedTimestamp = NeverRefreshed;
    private AdaptiveRefreshDecision? _lastAdaptiveDecision;

    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private readonly object _cacheLock = new();
    private readonly Dictionary<string, CachedUsage> _cache = new();
    private readonly List<string> _toolOrder = new();
    // Monotonic timestamps, so the intervals below survive a wall-clock change.
    private readonly Dictionary<string, long> _lastProviderAttemptTimestamps = new();
    private readonly Dictionary<string, long> _lastProviderFinishTimestamps = new();

    private long _lastManualRefreshStartedTimestamp = NeverRefreshed;
    private int _manualRefreshPending;
    private Task? _loopTask;
    // Volatile: read by refresh entry points that may resume off the UI thread (and by
    // tests exercising them cross-thread); the early-return must see Dispose's write
    // without a lock.
    private volatile bool _disposed;

    /// <summary>Raised after each refresh with the current cached state (on the UI thread).</summary>
    public event EventHandler<UsageState>? Updated;

    /// <summary>
    /// Raised (on the UI thread) when a refresh cycle starts fetching, with the display
    /// names of the tools being fetched; the matching <see cref="RefreshCompleted"/>
    /// concludes it. The pair drives the popover's small per-card refresh-in-progress
    /// indicator. <see cref="Updated"/> is NOT the conclusion: a debounced or
    /// gate-bypassed request re-emits the cached state mid-fetch, and clearing on that
    /// would hide the indicator while the fetch it reports is still running.
    /// </summary>
    public event EventHandler<IReadOnlyList<string>>? RefreshStarted;

    /// <summary>
    /// Raised (on the UI thread) when the fetch announced by <see cref="RefreshStarted"/>
    /// has finished, with the same tool names — on success, failure, and cancellation
    /// alike, so the indicator can never stick.
    /// </summary>
    public event EventHandler<IReadOnlyList<string>>? RefreshCompleted;
    public event EventHandler<ToolKind>? AuthenticationRequired;

    /// <summary>
    /// Raised when a tool's fetch succeeds, so a prior server-side rejection (which is
    /// sticky per token fingerprint) can be cleared once the token is accepted again.
    /// </summary>
    public event EventHandler<ToolKind>? AuthenticationRecovered;
    internal event EventHandler<(string Key, bool Accepted)>? AccountAuthenticationChanged;

    public UsageCoordinator(
        UsageService usageService,
        DispatcherQueue? dispatcher = null,
        IUsageCachePersistence? persistence = null,
        IUsageHistoryRecorder? history = null,
        TimeProvider? time = null,
        Func<AdaptiveRefreshSignals>? readAdaptiveSignals = null)
    {
        _usageService = usageService;
        _dispatcher = dispatcher;
        _persistence = persistence;
        _history = history;
        _time = time ?? TimeProvider.System;
        _readAdaptiveSignals = readAdaptiveSignals ?? (() => default);
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

        var registered = _usageService.GetRegisteredToolNames();
        foreach (var snapshot in _persistence.Load())
        {
            if (_cache.ContainsKey(snapshot.ToolName) || !registered.Contains(snapshot.ToolName))
            {
                continue;
            }
            _toolOrder.Add(snapshot.ToolName);
            _cache[snapshot.ToolName] = new CachedUsage
            {
                ToolName = snapshot.ToolName,
                Snapshot = snapshot,
                LastUpdatedAt = snapshot.IsSignedOut ? null : snapshot.CapturedAt,
                LastRefreshFailed = false,
                IsRehydrated = true,
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
    /// Requests a refresh. Manual requests wait for the active cycle and are
    /// debounced against other manual requests only.
    /// </summary>
    public async Task RefreshAsync(RefreshReason reason, string? accountKey = null)
    {
        // A wake/unlock handler can race DisposePipeline and call in after teardown;
        // that must be a quiet no-op, not an ObjectDisposedException from _cts.
        if (_disposed)
        {
            return;
        }

        // Record even a debounced open: interaction and a successful fetch are different signals.
        if (reason == RefreshReason.PopoverOpened)
            Interlocked.Exchange(ref _lastPopoverOpenedTimestamp, _time.GetTimestamp());

        if (reason == RefreshReason.Manual)
        {
            // Keep at most one explicit request waiting or running, even if the
            // current fetch takes longer than the debounce.
            if (Interlocked.Exchange(ref _manualRefreshPending, 1) != 0)
            {
                return;
            }
            try
            {
                await RefreshGuardedAsync(reason, waitForExisting: true, accountKey);
            }
            finally
            {
                Volatile.Write(ref _manualRefreshPending, 0);
            }
            return;
        }

        await RefreshGuardedAsync(
            reason,
            waitForExisting: reason is RefreshReason.AuthenticationChanged or RefreshReason.ToolsChanged,
            accountKey);
    }

    /// <summary>
    /// Runs one refresh cycle with every failure contained. Callers are async void UI
    /// handlers and the scheduler loop: an escaping exception would crash the process
    /// (or silently kill the loop), so cancellation and disposal are treated as normal
    /// shutdown races and anything unexpected is logged instead of rethrown.
    /// </summary>
    private async Task RefreshGuardedAsync(RefreshReason reason, bool waitForExisting = false, string? accountKey = null)
    {
        try
        {
            // _cts.Token is read inside the try: Dispose can tear the CTS down between
            // the _disposed check and here, and that must land in the catch below.
            // On the pool because popover opens and the refresh button call in from the UI
            // thread, and a cycle does blocking work: WMI queries, credential parsing, SQLite
            // commits and the cache write. Every event it raises goes through the dispatcher.
            var cancellationToken = _cts.Token;
            await Task.Run(() => RefreshCoreAsync(reason, cancellationToken, waitForExisting, accountKey), cancellationToken);
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
            using var timer = new PeriodicTimer(SchedulerTickInterval, _time);
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
        bool waitForExisting,
        string? accountKey)
    {
        var forcedRefresh = reason is RefreshReason.PopoverOpened or RefreshReason.SystemResumed;
        var requestedTimestamp = _time.GetTimestamp();
        // A scheduled tick can be dropped. An open or resume still owes a read to
        // providers the active cycle might not have fetched, so show cache and wait.
        var entered = waitForExisting
            ? await WaitForGateAsync(cancellationToken)
            : await _refreshGate.WaitAsync(0, cancellationToken);
        if (!entered)
        {
            EmitState();
            if (!forcedRefresh) return;
            await _refreshGate.WaitAsync(cancellationToken);
        }

        List<string>? fetchingTools = null;
        try
        {
            var attemptTimestamp = _time.GetTimestamp();
            if (reason == RefreshReason.Manual && _lastManualRefreshStartedTimestamp != NeverRefreshed
                && _time.GetElapsedTime(_lastManualRefreshStartedTimestamp, attemptTimestamp) < ForcedRefreshDebounce)
            {
                EmitState();
                return;
            }

            var enabledProviders = _usageService.GetEnabledProviders();
            if (accountKey is not null)
                enabledProviders = enabledProviders.Where(provider => provider.ToolName == accountKey).ToList();
            var registeredAtStart = _usageService.GetRegisteredToolNames();
            foreach (var key in _lastProviderAttemptTimestamps.Keys.Where(key => !registeredAtStart.Contains(key)).ToList())
            {
                _lastProviderAttemptTimestamps.Remove(key);
                _lastProviderFinishTimestamps.Remove(key);
            }
            var adaptive = reason == RefreshReason.Periodic ? GetAdaptiveDecision(attemptTimestamp) : default;
            var providersToRefresh = reason switch
            {
                RefreshReason.Periodic => enabledProviders
                    .Where(provider => IsDue(provider.ToolName, attemptTimestamp,
                        adaptive.IntervalFor(PeriodicIntervalFor(provider.Tool))))
                    .ToList(),
                RefreshReason.PopoverOpened => enabledProviders
                    .Where(provider => IsForcedRefreshDue(provider.ToolName, requestedTimestamp)
                        && IsDue(provider.ToolName, attemptTimestamp, ForcedIntervalFor(provider.Tool)))
                    .ToList(),
                RefreshReason.SystemResumed => enabledProviders
                    .Where(provider => IsForcedRefreshDue(provider.ToolName, requestedTimestamp))
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
                        _usageService.GetRegisteredToolNames());
                EmitState();
                if (removedAnyTool)
                {
                    PersistSuccessfulSnapshots();
                }
                return;
            }

            if (reason == RefreshReason.Manual)
            {
                _lastManualRefreshStartedTimestamp = attemptTimestamp;
            }
            foreach (var provider in providersToRefresh)
            {
                _lastProviderAttemptTimestamps[provider.ToolName] = attemptTimestamp;
            }

            fetchingTools = providersToRefresh.Select(provider => provider.ToolName).ToList();
            RaiseToolListEvent(RefreshStarted, fetchingTools);

            // Snapshot the cached capture-times before merging so a genuine live fetch can
            // be told apart from a re-served cache (see ReportAuthenticationOutcomes).
            Dictionary<string, DateTimeOffset?> priorCaptured;
            lock (_cacheLock)
            {
                priorCaptured = _cache.ToDictionary(kv => kv.Key, kv => kv.Value.Snapshot?.CapturedAt);
            }
            var attempted = await _usageService.GetSnapshotsAsync(
                providersToRefresh, cancellationToken, InteractionFor(reason), _time);
            foreach (var result in attempted)
                _lastProviderFinishTimestamps[result.ToolName] = result.CompletedTimestamp;
            var enabledToolNames = _usageService.GetRegisteredToolNames();
            var results = attempted.Where(result => enabledToolNames.Contains(result.ToolName)).ToList();
            var cacheChanged = MergeIntoCache(results, enabledToolNames);
            ReportAuthenticationOutcomes(results, priorCaptured);
            RecordHistory(results, priorCaptured);
            EmitState();
            if (cacheChanged)
            {
                PersistSuccessfulSnapshots();
            }
        }
        finally
        {
            // In the finally so failure and cancellation conclude the started fetch too —
            // an indicator that only cleared on success could stick forever.
            if (fetchingTools is not null)
            {
                RaiseToolListEvent(RefreshCompleted, fetchingTools);
            }
            _refreshGate.Release();
        }
    }

    private async Task<bool> WaitForGateAsync(CancellationToken cancellationToken)
    {
        await _refreshGate.WaitAsync(cancellationToken);
        return true;
    }

    private bool IsDue(string key, long nowTimestamp, TimeSpan interval)
        => interval <= TimeSpan.Zero
            || !_lastProviderAttemptTimestamps.TryGetValue(key, out var previous)
            || _time.GetElapsedTime(previous, nowTimestamp) >= interval;

    private bool IsForcedRefreshDue(string key, long requestedTimestamp)
        => !_lastProviderFinishTimestamps.TryGetValue(key, out var finished)
            || _time.GetElapsedTime(finished, requestedTimestamp) >= ForcedRefreshDebounce;

    private AdaptiveRefreshDecision GetAdaptiveDecision(long nowTimestamp)
    {
        var lastOpen = Interlocked.Read(ref _lastPopoverOpenedTimestamp);
        var decision = AdaptiveRefreshPolicy.Evaluate(lastOpen == NeverRefreshed
            ? null : _time.GetElapsedTime(lastOpen, nowTimestamp), _readAdaptiveSignals());
        if (decision != _lastAdaptiveDecision)
        {
            // Only policy transitions are logged, never account/provider identity or activity traces.
            DiagnosticsLog.Write("refresh", $"reason={decision.Reason} multiplier={decision.Multiplier} cap=30m");
            _lastAdaptiveDecision = decision;
        }
        return decision;
    }

    /// <summary>
    /// Provider-specific background cadence. User-triggered and authentication/tool changes
    /// bypass these intervals and still refresh every enabled provider immediately.
    /// </summary>
    internal static TimeSpan PeriodicIntervalFor(ToolKind tool) => tool switch
    {
        ToolKind.Codex => TimeSpan.FromMinutes(3),
        ToolKind.ClaudeCode => TimeSpan.FromMinutes(5),
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
    /// floor costs no meaningful freshness; the refresh button bypasses the floor.
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
            if (result.ToolName.Contains(ToolAccount.KeySeparator))
            {
                if (result.Error is AuthenticationRequiredException)
                    RaiseAccountAuthenticationChanged(result.ToolName, accepted: false);
                else if (IsLiveServerSuccess(result, priorCaptured))
                    RaiseAccountAuthenticationChanged(result.ToolName, accepted: true);
                continue;
            }

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
    /// HTTP providers distinguish accepted reads from cache serves and signed-out results.
    /// For other providers, a newer usage reading is evidence of a live fetch.
    /// </summary>
    private static bool IsLiveServerSuccess(
        ProviderSnapshotResult result, IReadOnlyDictionary<string, DateTimeOffset?> priorCaptured)
    {
        if (!result.Succeeded || result.Snapshot is not { } snapshot)
        {
            return false;
        }
        if (snapshot.IsLive is { } isLive) return isLive;
        return snapshot.HasUsage && IsNewCapture(snapshot, priorCaptured);
    }

    private static bool IsNewCapture(UsageSnapshot snapshot, IReadOnlyDictionary<string, DateTimeOffset?> priorCaptured)
        => !priorCaptured.TryGetValue(snapshot.ToolName, out var prior) || prior is null || snapshot.CapturedAt > prior.Value;

    /// <summary>
    /// History needs new usage readings. An accepted request may prove authentication even
    /// when its payload has no usage windows to record.
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
            if (result.Succeeded && result.Snapshot is { HasUsage: true, IsLive: not false } snapshot
                && IsNewCapture(snapshot, priorCaptured))
            {
                _history.Record(snapshot);
            }
        }
    }

    /// <summary>
    /// Which fetches a provider's rate-limit cooldown may hold back: explicit user
    /// actions (the refresh button, a completed sign-in, adding a tool) always get a
    /// real attempt, while the scheduler's cycles and the popover-open forced refresh
    /// are background traffic the cooldown exists to protect against.
    /// </summary>
    internal static FetchInteraction InteractionFor(RefreshReason reason) => reason switch
    {
        RefreshReason.Manual or RefreshReason.AuthenticationChanged or RefreshReason.ToolsChanged
            => FetchInteraction.UserInitiated,
        _ => FetchInteraction.Background,
    };

    private void Raise(EventHandler<ToolKind>? handler, ToolKind tool)
    {
        if (handler is null) return;
        if (_dispatcher is not null) _dispatcher.TryEnqueue(() => handler(this, tool));
        else handler(this, tool);
    }

    private void RaiseAccountAuthenticationChanged(string key, bool accepted)
    {
        var handler = AccountAuthenticationChanged;
        if (handler is null) return;
        if (_dispatcher is not null) _dispatcher.TryEnqueue(() => handler(this, (key, accepted)));
        else handler(this, (key, accepted));
    }

    private void RaiseToolListEvent(EventHandler<IReadOnlyList<string>>? handler, IReadOnlyList<string> toolNames)
    {
        if (handler is null) return;
        if (_dispatcher is not null) _dispatcher.TryEnqueue(() => handler(this, toolNames));
        else handler(this, toolNames);
    }

    /// <summary>Merges the results and reports whether the persisted snapshots changed.</summary>
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
            var cacheChanged = staleKeys.Count > 0;
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
                if (result.Skipped)
                {
                    _cache.TryAdd(result.ToolName, new CachedUsage { ToolName = result.ToolName });
                    continue;
                }
                var retained = result.Succeeded ? result.Snapshot : RetainSnapshot(result, previous?.Snapshot);
                cacheChanged |= result.Succeeded || !Equals(retained, previous?.Snapshot);
                _cache[result.ToolName] = new CachedUsage
                {
                    ToolName = result.ToolName,
                    Snapshot = retained,
                    LastUpdatedAt = retained is { IsSignedOut: false } ? retained.CapturedAt : null,
                    LastRefreshFailed = !result.Succeeded,
                    IsRehydrated = !result.Succeeded && retained is not null && previous?.IsRehydrated == true
                        && retained.CapturedAt == previous.Snapshot?.CapturedAt,
                };
            }

            return cacheChanged;
        }
    }

    private UsageSnapshot? RetainSnapshot(ProviderSnapshotResult result, UsageSnapshot? previous)
    {
        var metadata = result.Metadata;
        if (metadata?.AccountIdentity is { } current && previous?.AccountIdentity is { } prior && current != prior)
            previous = null;

        if (metadata?.VerifiedPlan is not { } plan) return previous;
        if (previous is null || previous.IsSignedOut)
        {
            return new UsageSnapshot
            {
                ToolName = result.ToolName, Windows = [], Plan = plan,
                AccountIdentity = metadata.AccountIdentity, CapturedAt = _time.GetUtcNow(), IsLive = false,
            };
        }
        return previous with { Plan = plan };
    }

    public void ReemitState() => EmitState();

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

        // Let the loop observe the cancel and unwind. Cycles run entirely on the thread
        // pool, so they exit in milliseconds; the bound only keeps a pathological hang
        // (a provider stuck in a blocking call) from stalling exit, and its held gate is
        // what the leak branch below exists for.
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
