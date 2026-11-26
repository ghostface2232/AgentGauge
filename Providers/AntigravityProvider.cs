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
/// public usage endpoint and no Gauge-owned credential: the data comes from an engine Gauge
/// launches itself (delegate), which authenticates with the on-disk login. Gauge does not
/// attach to the IDE's own engine: from Antigravity 2.35 the IDE keeps that engine's CSRF token
/// out of the process table on purpose, and reading it from elsewhere would defeat that
/// protection. The loopback Connect transport, the engine lifecycle, and parsing are isolated
/// behind this provider; callers see only a normalized <see cref="UsageSnapshot"/>.
///
/// Failure handling follows the coordinator's contract: not installed is a clean empty snapshot;
/// every other unusable outcome throws so the last good snapshot is preserved rather than
/// overwritten with nothing.
/// </summary>
public sealed class AntigravityProvider : IUsageProvider, IDisposable
{
    private readonly IAntigravityReader _engine;
    private readonly Func<bool> _isInstalled;
    private readonly IDisposable[] _owned;
    private readonly IAntigravityCliReader? _cli;

    public AntigravityProvider()
    {
        var installRoot = AntigravityInstall.DefaultRoot();
        var host = new AntigravityEngineHost(installRoot);

        _engine = new AntigravityDelegateReader(host);
        _isInstalled = () => installRoot is not null
            && File.Exists(AntigravityInstall.EngineExecutablePath(installRoot));
        _owned = new IDisposable[] { host };
        _cli = new AntigravityCliReader(new CliLocator());
    }

    internal AntigravityProvider(IAntigravityReader engine, Func<bool> isInstalled,
        IAntigravityCliReader? cli = null)
    {
        _engine = engine;
        _isInstalled = isInstalled;
        _owned = Array.Empty<IDisposable>();
        _cli = cli;
    }

    public ToolKind Tool => ToolKind.Antigravity;

    public string ToolName => ToolCatalog.For(ToolKind.Antigravity).DisplayName;

    public async Task<UsageSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
        => await GetSnapshotAsync(FetchInteraction.Background, cancellationToken);

    public async Task<UsageSnapshot> GetSnapshotAsync(FetchInteraction interaction, CancellationToken cancellationToken)
    {
        var hasEngine = _isInstalled();
        if (hasEngine && await _engine.ReadAsync(cancellationToken) is { } reading && ToSnapshot(reading) is { } snapshot)
            return snapshot;
        // An installed engine that could not read usage is a failure even if the CLI exists.
        if ((!hasEngine || interaction == FetchInteraction.UserInitiated) && _cli is { IsInstalled: true } cli)
        {
            // A signed-out CLI can ask to sign in, so only launch on an explicit refresh.
            if (interaction == FetchInteraction.Background) throw new UsageRefreshSkippedException();
            if (await cli.ReadAsync(cancellationToken) is { } report && ToSnapshot(report) is { } fallback)
                return fallback with { HistoryIdentity = "antigravity-cli", SourceLabel = "agy" };
        }
        else if (!hasEngine) return EmptySnapshot();
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
            PlanUnavailable = reading.PlanUnavailable,
            Windows = windows,
            CapturedAt = DateTimeOffset.Now,
        };
    }

    private UsageSnapshot EmptySnapshot() => new()
    {
        ToolName = ToolName,
        IsSignedOut = true,
        IsLive = false,
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
