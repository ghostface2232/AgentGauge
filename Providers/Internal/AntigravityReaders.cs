namespace Gauge.Providers.Internal;

/// <summary>
/// Reads usage by launching the engine ourselves (delegate mode). The lifecycle — spawn,
/// read, cleanup — lives in <see cref="AntigravityEngineHost"/>.
/// </summary>
internal sealed class AntigravityDelegateReader : IAntigravityReader
{
    private readonly AntigravityEngineHost _host;

    public AntigravityDelegateReader(AntigravityEngineHost host) => _host = host;

    public Task<AntigravityReading?> ReadAsync(CancellationToken cancellationToken)
        => _host.GetReadingAsync(cancellationToken);
}
