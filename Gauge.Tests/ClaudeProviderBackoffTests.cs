using System.Net;
using System.Text;
using Gauge.Models;
using Gauge.Providers;
using Gauge.Services;

namespace Gauge.Tests;

/// <summary>
/// Time-dependent Claude throttle behavior, driven through an injected clock: the 5-minute
/// happy-path cache, the 2→4→…→30-minute 429 escalation, and cooldown expiry. Closes the
/// "429 backoff timing" row from COVERAGE.md.
/// </summary>
public sealed class ClaudeProviderBackoffTests
{
    private static readonly DateTimeOffset Start = new(2026, 8, 3, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ServesCacheWithinFiveMinutesThenRefetches()
    {
        var handler = new SequenceHandler(Ok(), Ok());
        var (provider, time) = Provider(handler);

        await provider.GetSnapshotAsync(default);
        time.Now = Start.AddMinutes(4);
        await provider.GetSnapshotAsync(default);
        Assert.Equal(1, handler.Calls); // second read served from cache

        time.Now = Start.AddMinutes(5.5);
        await provider.GetSnapshotAsync(default);
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task CooldownEscalatesPer429AndExpires()
    {
        var handler = new SequenceHandler(Ok(), TooMany(), TooMany(), Ok());
        var (provider, time) = Provider(handler);

        await provider.GetSnapshotAsync(default); // live success cached
        time.Now = Start.AddMinutes(6);
        var afterFirst429 = await provider.GetSnapshotAsync(default); // 429 #1 → 2m cooldown
        Assert.Equal(2, handler.Calls);
        Assert.NotEmpty(afterFirst429.Windows); // last good value kept on screen

        time.Now = Start.AddMinutes(7); // inside the 2m cooldown → no network
        await provider.GetSnapshotAsync(default);
        Assert.Equal(2, handler.Calls);

        time.Now = Start.AddMinutes(8.5); // past it → retries, hits 429 #2 → 4m cooldown
        await provider.GetSnapshotAsync(default);
        Assert.Equal(3, handler.Calls);

        time.Now = Start.AddMinutes(11.5); // inside the 4m cooldown
        await provider.GetSnapshotAsync(default);
        Assert.Equal(3, handler.Calls);

        time.Now = Start.AddMinutes(13); // past it → success resets the escalation
        var recovered = await provider.GetSnapshotAsync(default);
        Assert.Equal(4, handler.Calls);
        Assert.NotEmpty(recovered.Windows);
    }

    private static (ClaudeProvider Provider, MutableTime Time) Provider(SequenceHandler handler)
    {
        var time = new MutableTime(Start);
        var provider = new ClaudeProvider(new HttpClient(handler), Available(), time: time);
        return (provider, time);
    }

    private static (string Json, HttpStatusCode Status) Ok()
        => ("""{ "five_hour": { "utilization": 42 } }""", HttpStatusCode.OK);

    private static (string Json, HttpStatusCode Status) TooMany()
        => ("{}", HttpStatusCode.TooManyRequests);

    private static ICredentialSource Available() => new StubSource(
        new CredentialReadResult
        {
            Tool = ToolKind.ClaudeCode, Status = CredentialReadStatus.Available,
            Credential = new ToolCredential
            {
                Tool = ToolKind.ClaudeCode, Owner = CredentialOwner.CliLocal, Source = CredentialSource.CliLocal,
                AccessToken = "token",
            },
        });

    private sealed class MutableTime(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class StubSource(CredentialReadResult result) : ICredentialSource
    {
        public CredentialOwner Owner => CredentialOwner.CliLocal;
        public CredentialSource Source => CredentialSource.CliLocal;
        public Task<CredentialReadResult> ReadAsync(ToolKind tool, CancellationToken cancellationToken = default)
            => Task.FromResult(result);
    }

    private sealed class SequenceHandler(params (string Json, HttpStatusCode Status)[] responses) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var (json, status) = responses[Math.Min(Calls, responses.Length - 1)];
            Calls++;
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            });
        }
    }
}
