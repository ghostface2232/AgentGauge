namespace Gauge.Providers.Internal;

/// <summary>
/// Decides whether the self-signed certificate of an Antigravity language server may be
/// accepted. The server listens on loopback with a self-signed cert, so normal validation
/// fails; we make a narrow exception, but ONLY for the loopback IP literal we connect to
/// (<c>127.0.0.1</c>). "localhost" and every non-loopback host are rejected so this insecure
/// exception can never apply to a real, off-machine destination.
/// </summary>
internal static class AntigravityLoopbackTls
{
    public static bool IsTrustedLoopback(Uri? requestUri)
        => requestUri is not null
            && requestUri.IsLoopback
            && string.Equals(requestUri.Host, "127.0.0.1", StringComparison.Ordinal);
}
