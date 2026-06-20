using System.Net;
using System.Text;
using Gauge.Models;
using Gauge.Providers;
using Gauge.Services;

namespace Gauge.Tests;

/// <summary>
/// When the local Codex token is expired (a ~10-day JWT past its exp), the provider asks
/// the refresher to nudge the CLI (codex doctor) and then re-reads the freshened
/// credentials instead of immediately failing.
/// </summary>
public sealed class CodexDelegatedRefreshTests
{
    private const string UsageJson = """{ "rate_limit": { "primary_window": { "used_percent": 25 } } }""";

    [Fact]
    public async Task ExpiredTokenIsRecoveredViaDelegatedRefresh()
    {
        var source = new SequencedSource(Invalid(), Available("fresh-token"));
        var refresher = new FakeRefresher(ran: true);
        var provider = new CodexProvider(new HttpClient(new StubHandler(UsageJson)), source, refresher);

        var snapshot = await provider.GetSnapshotAsync(default);

        Assert.Equal(1, refresher.Calls);
        Assert.Equal(2, source.Reads); // initial + post-refresh re-read
        Assert.Single(snapshot.Windows);
    }

    [Fact]
    public async Task StillInvalidAfterRefreshThrowsAuthenticationRequired()
    {
        var source = new SequencedSource(Invalid(), Invalid());
        var refresher = new FakeRefresher(ran: true);
        var provider = new CodexProvider(new HttpClient(new StubHandler(UsageJson)), source, refresher);

        await Assert.ThrowsAsync<AuthenticationRequiredException>(() => provider.GetSnapshotAsync(default));
        Assert.Equal(1, refresher.Calls);
    }

    [Fact]
    public async Task SkippedRefreshDoesNotReReadAndStillThrows()
    {
        var source = new SequencedSource(Invalid(), Available("unused"));
        var refresher = new FakeRefresher(ran: false); // cooldown / no CLI
        var provider = new CodexProvider(new HttpClient(new StubHandler(UsageJson)), source, refresher);

        await Assert.ThrowsAsync<AuthenticationRequiredException>(() => provider.GetSnapshotAsync(default));
        Assert.Equal(1, refresher.Calls);
        Assert.Equal(1, source.Reads); // no re-read when the refresh was skipped
    }

    [Fact]
    public async Task ServerRejectionTriggersRefreshAndRetriesFetch()
    {
        // Local token looks valid, but the server returns 401 on the first call; after a
        // delegated refresh the re-read token succeeds on retry.
        var source = new SequencedSource(Available("stale-token"), Available("fresh-token"));
        var refresher = new FakeRefresher(ran: true);
        var handler = new StubHandler(UsageJson) { FirstStatus = HttpStatusCode.Unauthorized };
        var provider = new CodexProvider(new HttpClient(handler), source, refresher);

        var snapshot = await provider.GetSnapshotAsync(default);

        Assert.Equal(1, refresher.Calls);
        Assert.Equal(2, handler.Calls); // failed call + retry
        Assert.Single(snapshot.Windows);
    }

    [Fact]
    public async Task NoRefresherStillThrowsOnInvalidCredential()
    {
        // Without a refresher (the optional dependency omitted) the old behavior holds.
        var source = new SequencedSource(Invalid());
        var provider = new CodexProvider(new HttpClient(new StubHandler(UsageJson)), source);

        await Assert.ThrowsAsync<AuthenticationRequiredException>(() => provider.GetSnapshotAsync(default));
        Assert.Equal(1, source.Reads);
    }

    private static CredentialReadResult Available(string token) => new()
    {
        Tool = ToolKind.Codex, Status = CredentialReadStatus.Available,
        Credential = new ToolCredential
        {
            Tool = ToolKind.Codex, Owner = CredentialOwner.CliLocal, Source = CredentialSource.CliLocal,
            AccessToken = token,
        },
    };

    private static CredentialReadResult Invalid() => new()
    {
        Tool = ToolKind.Codex, Status = CredentialReadStatus.Invalid,
    };

    /// <summary>Returns each supplied result in turn, repeating the last one thereafter.</summary>
    private sealed class SequencedSource(params CredentialReadResult[] results) : ICredentialSource
    {
        public int Reads { get; private set; }
        public CredentialOwner Owner => CredentialOwner.CliLocal;
        public CredentialSource Source => CredentialSource.CliLocal;
        public Task<CredentialReadResult> ReadAsync(ToolKind tool, CancellationToken cancellationToken = default)
        {
            var index = Math.Min(Reads, results.Length - 1);
            Reads++;
            return Task.FromResult(results[index]);
        }
    }

    private sealed class FakeRefresher(bool ran) : IDelegatedTokenRefresher
    {
        public int Calls { get; private set; }
        public Task<bool> TryRefreshAsync(CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(ran);
        }
    }

    private sealed class StubHandler(string json) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public HttpStatusCode? FirstStatus { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var status = Calls == 0 && FirstStatus is { } first ? first : HttpStatusCode.OK;
            Calls++;
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            });
        }
    }
}
