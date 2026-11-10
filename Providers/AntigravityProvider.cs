using Gauge.Models;
using Gauge.Providers.Internal;
using Gauge.Services;

namespace Gauge.Providers;

/// <summary>
/// Raised when Antigravity is installed but no engine could be reached or it returned no usable
/// usage (signed out, still warming, a transport error, or an availability-only response). It is
/// a provider failure, so the coordinator keeps the last good snapshot instead of clearing the
/// card — distinct from "not installed", which is a clean empty result.
/// </summary>
public sealed class AntigravityUnavailableException : Exception
{
    public AntigravityUnavailableException(string message) : base(message)
    {
    }
}

/// <summary>
/// Reads Antigravity usage from its local language server. Unlike the other tools there is no
/// public usage endpoint and no Gauge-owned credential: the data comes from the engine the IDE
/// runs (attach) or, when the IDE is closed, from an engine Gauge launches itself (delegate),
/// which authenticates with the on-disk login. All of that — process discovery, the loopback
/// Connect transport, the delegate engine lifecycle, and parsing — is isolated behind this
/// provider; callers see only a normalized <see cref="UsageSnapshot"/>.
///
/// Failure handling follows the coordinator's contract: not installed is a clean empty snapshot;
/// every other unusable outcome throws so the last good snapshot is preserved rather than
/// overwritten with nothing.
/// </summary>
public sealed class AntigravityProvider : IUsageProvider, IDisposable
{
    private readonly IReadOnlyList<IAntigravityReader> _sources;
    private readonly Func<bool> _isInstalled;
    private readonly IDisposable[] _owned;
    private readonly AntigravityCliReader? _cli;

    public AntigravityProvider()
    {
        var installRoot = AntigravityInstall.DefaultRoot();
        var client = new AntigravityLoopbackClient();
        var host = new AntigravityEngineHost(installRoot);

        // Attach to a running IDE first; fall back to launching the engine ourselves.
        _sources = new IAntigravityReader[]
        {
            new AntigravityAttachReader(new AntigravityProcessDiscovery(installRoot), client),
            new AntigravityDelegateReader(host),
        };
        _isInstalled = () => installRoot is not null
            && File.Exists(AntigravityInstall.EngineExecutablePath(installRoot));
        _owned = new IDisposable[] { host, client };
        _cli = new AntigravityCliReader(new CliLocator());
    }

    internal AntigravityProvider(IReadOnlyList<IAntigravityReader> sources, Func<bool> isInstalled)
    {
        _sources = sources;
        _isInstalled = isInstalled;
        _owned = Array.Empty<IDisposable>();
    }

    public ToolKind Tool => ToolKind.Antigravity;

    public string ToolName => ToolCatalog.For(ToolKind.Antigravity).DisplayName;

    public async Task<UsageSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
        => await GetSnapshotAsync(FetchInteraction.Background, cancellationToken);

    public async Task<UsageSnapshot> GetSnapshotAsync(FetchInteraction interaction, CancellationToken cancellationToken)
    {
        // Keep CLI launches on explicit refreshes, since a signed-out CLI can ask to sign in.
        var cli = interaction == FetchInteraction.UserInitiated && _cli?.IsInstalled == true ? _cli : null;
        // Not installed: a clean "no data" state, not a failure.
        var hasEngine = _isInstalled();
        if (!hasEngine && cli is null)
        {
            if (_cli?.IsInstalled == true)
                throw new AntigravityUnavailableException("Refresh Antigravity manually to query the installed CLI.");
            return EmptySnapshot();
        }

        foreach (var source in hasEngine ? _sources : Array.Empty<IAntigravityReader>())
        {
            if (await source.ReadAsync(cancellationToken) is { } reading && ToSnapshot(reading) is { } snapshot)
                return snapshot;
        }
        if (cli is not null && await cli.ReadAsync(cancellationToken) is { } report && ToSnapshot(report) is { } fallback)
            return fallback;
        throw new AntigravityUnavailableException("Antigravity returned no usage windows.");
    }

    private UsageSnapshot? ToSnapshot(AntigravityReading reading)
    {
        IReadOnlyList<UsageWindow> windows;
        try
        {
            windows = AntigravityQuotaParser.Parse(reading.QuotaJson);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
        if (windows.Count == 0) return null;

        return new UsageSnapshot
        {
            ToolName = ToolName,
            Plan = reading.Plan,
            Windows = windows,
            CapturedAt = DateTimeOffset.Now,
        };
    }

    private UsageSnapshot EmptySnapshot() => new()
    {
        ToolName = ToolName,
        Plan = null,
        Windows = Array.Empty<UsageWindow>(),
        CapturedAt = DateTimeOffset.Now,
    };

    public void Dispose()
    {
        foreach (var disposable in _owned)
        {
            disposable.Dispose();
        }
    }
}
