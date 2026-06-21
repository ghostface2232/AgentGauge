using Gauge.Providers.Internal;

namespace Gauge.Tests;

/// <summary>
/// Loopback TLS gating and Connect URL construction. The actual network exchange is verified
/// live against a running language server, not here.
/// </summary>
public sealed class AntigravityLoopbackClientTests
{
    [Fact]
    public void TrustsSelfSignedCertOnlyForLoopbackIpLiteral()
    {
        Assert.True(AntigravityLoopbackTls.IsTrustedLoopback(new Uri("https://127.0.0.1:6112/x")));
    }

    [Theory]
    [InlineData("https://localhost:6112/x")]      // look-alike host, not the IP literal
    [InlineData("https://127.0.0.2:6112/x")]       // other loopback-range address
    [InlineData("https://10.0.0.5:6112/x")]        // LAN
    [InlineData("https://example.com/x")]          // public
    public void RejectsSelfSignedCertForNonLoopbackTargets(string uri)
    {
        Assert.False(AntigravityLoopbackTls.IsTrustedLoopback(new Uri(uri)));
    }

    [Fact]
    public void RejectsNullUri()
    {
        Assert.False(AntigravityLoopbackTls.IsTrustedLoopback(null));
    }

    [Fact]
    public void BuildsConnectServiceUri()
    {
        var uri = AntigravityLoopbackClient.BuildRequestUri(6112, "RetrieveUserQuotaSummary");

        Assert.Equal("https", uri.Scheme);
        Assert.Equal("127.0.0.1", uri.Host);
        Assert.Equal(6112, uri.Port);
        Assert.Equal(
            "/exa.language_server_pb.LanguageServerService/RetrieveUserQuotaSummary",
            uri.AbsolutePath);
    }
}
