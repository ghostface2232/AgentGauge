using System.Net.Http;
using System.Text.Json;
using Gauge.Models;

namespace Gauge.Services;

/// <summary>Unauthenticated public status polling. Failures retain the last observation as stale.</summary>
public sealed class ProviderStatusService : IDisposable
{
    public static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(15);
    private readonly HttpClient _http;
    private readonly Func<ToolKind, bool> _isEnabled;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private readonly Dictionary<ToolKind, long> _attempts = new();
    private readonly Dictionary<ToolKind, ProviderStatus> _statuses = new();
    private Task? _loop;
    private volatile bool _disposed;

    public event EventHandler<IReadOnlyList<ProviderStatus>>? Updated;

    // The caller supplies a dedicated client with no credential-bearing default headers.
    public ProviderStatusService(HttpClient http, Func<ToolKind, bool> isEnabled, TimeProvider? time = null)
        => (_http, _isEnabled, _time) = (http, isEnabled, time ?? TimeProvider.System);

    public void Start() => _loop ??= Task.Run(async () =>
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1), _time);
            do { await RefreshAsync().ConfigureAwait(false); }
            while (await timer.WaitForNextTickAsync(_stop.Token).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
    });

    public async Task RefreshAsync()
    {
        if (_disposed || !await _gate.WaitAsync(0).ConfigureAwait(false)) return;
        try
        {
            var enabled = ToolCatalog.All.Where(d => d.StatusPageUrl is not null && _isEnabled(d.Kind)).ToList();
            foreach (var removed in _statuses.Keys.Where(k => !enabled.Any(d => d.Kind == k)).ToList())
            {
                _statuses.Remove(removed);
                _attempts.Remove(removed);
            }
            var due = enabled.Where(d => !_attempts.TryGetValue(d.Kind, out var last)
                || _time.GetElapsedTime(last) >= PollInterval).ToList();
            foreach (var descriptor in due) _attempts[descriptor.Kind] = _time.GetTimestamp();
            var results = await Task.WhenAll(due.Select(FetchAsync)).ConfigureAwait(false);
            foreach (var result in results) _statuses[result.Tool] = result;
            if (!_disposed) Updated?.Invoke(this, _statuses.Values.Where(s => _isEnabled(s.Tool)).ToArray());
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception ex)
        {
            DiagnosticsLog.Write("status", $"Poll failed: {ex.GetType().Name}");
        }
        finally { _gate.Release(); }
    }

    private async Task<ProviderStatus> FetchAsync(ToolDescriptor descriptor)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            using var response = await _http.GetAsync(new Uri(descriptor.StatusPageUrl!, "api/v2/status.json"), timeout.Token)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false));
            return Parse(descriptor.Kind, json.RootElement, _time.GetUtcNow());
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            // Public response text is deliberately excluded from diagnostics.
            DiagnosticsLog.Write("status", $"Public status unavailable: {ex.GetType().Name}");
            return _statuses.TryGetValue(descriptor.Kind, out var previous)
                ? previous with { LastCheckFailed = true }
                : new(descriptor.Kind, ProviderStatusLevel.Unknown, "", null, true);
        }
    }

    public static ProviderStatus Parse(ToolKind tool, JsonElement root, DateTimeOffset checkedAt)
    {
        var status = root.GetProperty("status");
        var level = status.GetProperty("indicator").GetString() switch
        {
            "none" => ProviderStatusLevel.Operational,
            "minor" => ProviderStatusLevel.Minor,
            "major" => ProviderStatusLevel.Major,
            "critical" => ProviderStatusLevel.Critical,
            "maintenance" => ProviderStatusLevel.Maintenance,
            _ => throw new JsonException("Unknown public status indicator."),
        };
        var summary = status.TryGetProperty("description", out var description)
            && description.ValueKind == JsonValueKind.String ? description.GetString() ?? "" : "";
        // Keep untrusted public descriptions bounded and single-line on UI surfaces.
        summary = string.Join(" ", summary.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (summary.Length > 300) summary = summary[..300];
        return new(tool, level, summary, checkedAt);
    }

    public void Dispose()
    {
        _disposed = true;
        _stop.Cancel();
        // In-flight calls own the gate/CTS until unwound; do not dispose beneath them.
    }
}
