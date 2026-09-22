using Gauge.Models;
using Microsoft.UI.Dispatching;

namespace Gauge.Services.ApiCost;

/// <summary>
/// Runs the <see cref="ApiCostScanner"/> off the UI thread while the API-cost option is on
/// and publishes each result on the UI thread. Scans happen on enable, every fifteen
/// minutes after that, and when the popover opens — the latter debounced to one per
/// minute, since the logs only change while a CLI is working. While the option is off
/// nothing is read at all; the ledger on disk is simply left where it is.
/// </summary>
public sealed class ApiCostService : IDisposable
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan OpenDebounce = TimeSpan.FromMinutes(1);
    private const int MaxPassesPerScan = 64;

    private readonly ApiCostScanner _scanner;
    private readonly DispatcherQueue _dispatcher;
    private readonly Func<IReadOnlySet<string>> _tools;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private CancellationTokenSource? _loop;
    // The enabled loop's token, readable from the scan thread without touching the source
    // (which SetEnabled(false) disposes). Default — never cancellable — while disabled.
    private CancellationToken _activeToken;
    // Set when a scan was asked for while another held the gate; the running one re-runs
    // once it finishes, so toggling off and on mid-scan still produces a fresh result.
    private volatile bool _rerunRequested;
    private long _lastScanTimestamp;
    private bool _disposed;

    public ApiCostService(ApiCostScanner scanner, DispatcherQueue dispatcher, Func<IReadOnlySet<string>> tools, TimeProvider? time = null)
    {
        _scanner = scanner;
        _dispatcher = dispatcher;
        _tools = tools;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Latest estimates, one per scanned tool. Raised on the UI thread.</summary>
    public event EventHandler<IReadOnlyList<ApiCostEstimate>>? Updated;

    public bool IsEnabled => _loop is not null;

    /// <summary>Starts or stops scanning. Enabling scans immediately.</summary>
    public void SetEnabled(bool enabled)
    {
        if (_disposed) return;
        if (enabled == IsEnabled) return;
        if (!enabled)
        {
            _activeToken = default;
            _loop?.Cancel();
            _loop?.Dispose();
            _loop = null;
            return;
        }
        _loop = new CancellationTokenSource();
        _activeToken = _loop.Token;
        _ = RunLoopAsync(_activeToken);
    }

    /// <summary>A popover open: scan if the last one is old enough (no-op while disabled).</summary>
    public void RequestScan()
    {
        if (!IsEnabled || _loop is null) return;
        if (_lastScanTimestamp != 0 && _time.GetElapsedTime(_lastScanTimestamp) < OpenDebounce) return;
        _ = ScanOnceAsync(_activeToken);
    }

    private async Task RunLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await ScanOnceAsync(cancellationToken).ConfigureAwait(false);
            using var timer = new PeriodicTimer(Interval, _time);
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                await ScanOnceAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Disabled or disposed.
        }
    }

    private async Task ScanOnceAsync(CancellationToken cancellationToken)
    {
        if (!await _gate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            // A scan is already running — possibly one the user just cancelled by switching
            // the option off and on. Ask it to run again when it finishes.
            _rerunRequested = true;
            return;
        }
        try
        {
            _lastScanTimestamp = _time.GetTimestamp();
            var tools = _tools();
            // A budget-limited scan undercounts; showing it would put a number on screen
            // that later jumps. Scan again (each resumes where the last stopped and saves its
            // progress) and publish only a complete result. The pass cap is a backstop, not an
            // expected limit: each pass reads up to 512 MB, so it covers tens of gigabytes.
            var estimates = await Task.Run(() =>
            {
                var result = _scanner.Scan(tools, cancellationToken);
                for (var pass = 1; !_scanner.LastScanComplete && pass < MaxPassesPerScan; pass++)
                {
                    result = _scanner.Scan(tools, cancellationToken);
                }
                return _scanner.LastScanComplete ? result : null;
            }, cancellationToken).ConfigureAwait(false);
            if (cancellationToken.IsCancellationRequested || estimates is null) return;
            _dispatcher.TryEnqueue(() =>
            {
                // Checked again on the UI thread, where switching off cancels and clears: a
                // result queued just before that must not bring the chips back.
                if (!_disposed && !cancellationToken.IsCancellationRequested) Updated?.Invoke(this, estimates);
            });
        }
        catch (OperationCanceledException)
        {
            // Disabled mid-scan: the ledger keeps whatever was saved.
        }
        catch (Exception ex)
        {
            // The estimate is a convenience; nothing about usage display depends on it.
            DiagnosticsLog.Write("apicost", $"Scan failed: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            _gate.Release();
            if (_rerunRequested)
            {
                _rerunRequested = false;
                var active = _activeToken;
                if (active.CanBeCanceled && !active.IsCancellationRequested && !_disposed)
                {
                    _ = ScanOnceAsync(active);
                }
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _activeToken = default;
        _loop?.Cancel();
        _loop?.Dispose();
        _loop = null;
    }
}
