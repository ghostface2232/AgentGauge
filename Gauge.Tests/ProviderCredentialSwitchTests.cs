using System.Net;
using System.Text;
using Gauge.Models;
using Gauge.Providers;
using Gauge.Services;

namespace Gauge.Tests;

public sealed class ProviderCredentialSwitchTests
{
    [Fact]
    public async Task ClaudeAccountSwitchInvalidatesProviderCache()
    {
        var source = new MutableSource("token-one");
        var handler = new CountingHandler();
        var provider = new ClaudeProvider(new HttpClient(handler), source);
        await provider.GetSnapshotAsync(default);
        await provider.GetSnapshotAsync(default);
        Assert.Equal(1, handler.CallCount);

        source.Token = "token-two";
        await provider.GetSnapshotAsync(default);
        Assert.Equal(2, handler.CallCount);
    }

    [Fact]
    public async Task UnauthorizedUsageBecomesAuthenticationRequired()
    {
        var source = new MutableSource("expired");
        var handler = new CountingHandler(HttpStatusCode.Unauthorized);
        var provider = new ClaudeProvider(new HttpClient(handler), source);
        var error = await Assert.ThrowsAsync<AuthenticationRequiredException>(() => provider.GetSnapshotAsync(default));
        Assert.Equal(ToolKind.ClaudeCode, error.Tool);
    }

    private sealed class MutableSource(string token) : ICredentialSource
    {
        public string Token { get; set; } = token;
        public CredentialOwner Owner => CredentialOwner.CliLocal;
        public CredentialSource Source => CredentialSource.CliLocal;
        public Task<CredentialReadResult> ReadAsync(ToolKind tool, CancellationToken cancellationToken = default)
            => Task.FromResult(new CredentialReadResult
            {
                Tool = tool, Status = CredentialReadStatus.Available,
                Credential = new ToolCredential { Tool = tool, Owner = Owner, Source = Source, AccessToken = Token },
            });
    }

    private sealed class CountingHandler(HttpStatusCode statusCode = HttpStatusCode.OK) : HttpMessageHandler
    {
        public int CallCount { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(new HttpResponseMessage(statusCode)
            {
                Content = new StringContent("{\"five_hour\":{\"utilization\":10}}", Encoding.UTF8, "application/json"),
            });
        }
    }
}
