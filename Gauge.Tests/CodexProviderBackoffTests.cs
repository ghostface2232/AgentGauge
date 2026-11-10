using System.Net;
using System.Text;
using Gauge.Models;
using Gauge.Providers;
using Gauge.Services;

namespace Gauge.Tests;

/// <summary>
/// Codex under the shared retry layer: a retryable status arms a cooldown that holds
/// back background fetches (the popover-open forced refresh included) and serves the
/// cached snapshot, the server's Retry-After sets the cooldown when sent, and a
/// user-initiated refresh always goes out. Non-retryable failures keep propagating so
/// the coordinator's stale marker still works.
/// </summary>
public sealed class CodexProviderBackoffTests
{
    [Fact]
    public async Task RetryableFailureServesCachedAndCoolsDownBackgroundFetches()
    {
        var handler = new SequenceHandler(Ok(), TooMany(), Ok());
        var (provider, time) = Provider(handler);

        await provider.GetSnapshotAsync(default); // live success cached
        time.Advance(TimeSpan.FromMinutes(3));
        var throttled = await provider.GetSnapshotAsync(default); // 429 → 2m cooldown
        Assert.Equal(2, handler.Calls);
        Assert.NotEmpty(throttled.Windows); // last good value kept on screen

        time.Advance(TimeSpan.FromMinutes(1)); // inside the cooldown → no network
        await provider.GetSnapshotAsync(default);
        Assert.Equal(2, handler.Calls);

        time.Advance(TimeSpan.FromMinutes(1.5)); // past it → refetches live
        var recovered = await provider.GetSnapshotAsync(default);
        Assert.Equal(3, handler.Calls);
        Assert.NotEmpty(recovered.Windows);
    }

    [Fact]
    public async Task RetryAfterHeaderSetsTheCooldown()
    {
        var handler = new SequenceHandler(Ok(), TooMany(retryAfterSeconds: 300), Ok());
        var (provider, time) = Provider(handler);

        await provider.GetSnapshotAsync(default);
        time.Advance(TimeSpan.FromMinutes(3));
        await provider.GetSnapshotAsync(default); // 429 with Retry-After: 300

        time.Advance(TimeSpan.FromMinutes(4)); // past the 2m fallback, inside the 5m header
        await provider.GetSnapshotAsync(default);
        Assert.Equal(2, handler.Calls);

        time.Advance(TimeSpan.FromMinutes(1.5)); // past the header's schedule
        await provider.GetSnapshotAsync(default);
        Assert.Equal(3, handler.Calls);
    }

    [Fact]
    public async Task CreditRetryAfterSurvivesBalanceChangesWithoutBlockingUsage()
    {
        static Response Usage(int balance) => new($$"""
            { "rate_limit": { "primary_window": { "used_percent": 42 } },
              "rate_limit_reset_credits": { "available_count": {{balance}} } }
            """, HttpStatusCode.OK);
        var credits = new Response("""{ "credits": [{ "status": "available", "expires_at": "2027-03-09T07:59:00Z" }] }""", HttpStatusCode.OK);
        var handler = new SequenceHandler(Usage(1), TooMany(retryAfterSeconds: 3600), Usage(2), Usage(2), credits);
        var (provider, time) = Provider(handler);

        Assert.Null((await provider.GetSnapshotAsync(default)).ResetCreditDeadlines);
        time.Advance(TimeSpan.FromSeconds(3599));
        var duringCooldown = await provider.GetSnapshotAsync(FetchInteraction.UserInitiated, default);
        Assert.Single(duringCooldown.Windows);
        Assert.Equal(2, duringCooldown.ResetCredits);
        Assert.Equal(3, handler.Calls);

        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Single((await provider.GetSnapshotAsync(default)).ResetCreditDeadlines!);
        Assert.Equal(5, handler.Calls);
    }

    [Fact]
    public async Task UserInitiatedRefreshBypassesTheCooldown()
    {
        var handler = new SequenceHandler(Ok(), TooMany(), Ok());
        var (provider, time) = Provider(handler);

        await provider.GetSnapshotAsync(default);
        time.Advance(TimeSpan.FromMinutes(3));
        await provider.GetSnapshotAsync(default); // 429 → cooldown

        time.Advance(TimeSpan.FromSeconds(30)); // deep inside the cooldown
        await provider.GetSnapshotAsync(default); // background: blocked
        Assert.Equal(2, handler.Calls);

        var manual = await provider.GetSnapshotAsync(FetchInteraction.UserInitiated, default);
        Assert.Equal(3, handler.Calls); // the user's click got a real attempt
        Assert.NotEmpty(manual.Windows);
    }

    [Fact]
    public async Task NonRetryableFailureStillPropagatesDespiteCache()
    {
        var handler = new SequenceHandler(Ok(), NotFound());
        var (provider, time) = Provider(handler);

        await provider.GetSnapshotAsync(default);
        time.Advance(TimeSpan.FromMinutes(3));

        // Codex does not serve its cache on arbitrary failures — those propagate so the
        // coordinator keeps the last good snapshot and marks the card stale.
        await Assert.ThrowsAnyAsync<HttpRequestException>(() => provider.GetSnapshotAsync(default));
    }

    [Fact]
    public async Task ColdStartRetryableFailurePropagatesWhenNothingCached()
    {
        var (provider, _) = Provider(new SequenceHandler(TooMany()));
        await Assert.ThrowsAnyAsync<HttpRequestException>(() => provider.GetSnapshotAsync(default));
    }

    [Fact]
    public async Task CooldownWithNothingCachedFailsFastWithoutANetworkCall()
    {
        var handler = new SequenceHandler(TooMany(retryAfterSeconds: 600), Ok());
        var (provider, time) = Provider(handler);

        await Assert.ThrowsAnyAsync<HttpRequestException>(() => provider.GetSnapshotAsync(default));
        Assert.Equal(1, handler.Calls);

        // Background attempts inside the cooldown must not touch the throttling endpoint
        // even with nothing cached — this is the cold-start case the cooldown protects.
        time.Advance(TimeSpan.FromMinutes(3));
        await Assert.ThrowsAnyAsync<HttpRequestException>(() => provider.GetSnapshotAsync(default));
        Assert.Equal(1, handler.Calls);

        // A user-initiated refresh still gets a real attempt.
        var manual = await provider.GetSnapshotAsync(FetchInteraction.UserInitiated, default);
        Assert.Equal(2, handler.Calls);
        Assert.NotEmpty(manual.Windows);
    }

    [Fact]
    public async Task RetryableFailureOnThePostRefreshRetryStillArmsTheCooldown()
    {
        // 401 → delegated refresh → the single retry hits 429. The retry goes through
        // the same policy layer as the first attempt, so the gate arms and the cached
        // snapshot is served rather than the 429 escaping both.
        var handler = new SequenceHandler(Ok(), Unauthorized(), TooMany(), Ok());
        var time = new MutableTime();
        var provider = new CodexProvider(
            new HttpClient(handler), new MutableSource("token"), new FakeRefresher(), time);

        await provider.GetSnapshotAsync(default); // live success cached
        time.Advance(TimeSpan.FromMinutes(3));
        var throttled = await provider.GetSnapshotAsync(default); // 401 → refresh → retry 429
        Assert.Equal(3, handler.Calls);
        Assert.NotEmpty(throttled.Windows); // cache served, not a hard failure

        time.Advance(TimeSpan.FromMinutes(1)); // inside the retry-armed cooldown → no network
        await provider.GetSnapshotAsync(default);
        Assert.Equal(3, handler.Calls);

        time.Advance(TimeSpan.FromMinutes(1.5)); // past it → refetches live
        await provider.GetSnapshotAsync(default);
        Assert.Equal(4, handler.Calls);
    }

    [Fact]
    public async Task AccountSwitchClearsTheCooldownAndCache()
    {
        var handler = new SequenceHandler(Ok(), TooMany(), Ok());
        var time = new MutableTime();
        var source = new MutableSource("token-one");
        var provider = new CodexProvider(new HttpClient(handler), source, time: time);

        await provider.GetSnapshotAsync(default);
        time.Advance(TimeSpan.FromMinutes(3));
        await provider.GetSnapshotAsync(default); // 429 → cooldown

        source.Token = "token-two";
        await provider.GetSnapshotAsync(default); // new account must not inherit the block
        Assert.Equal(3, handler.Calls);
    }

    [Fact]
    public async Task AnInvalidReadKeepsTheCooldownAndCache()
    {
        var (provider, source, handler, time) = ThrottledProvider();
        await provider.GetSnapshotAsync(default);
        time.Advance(TimeSpan.FromMinutes(3));
        await provider.GetSnapshotAsync(default); // 429 → cooldown

        source.Status = CredentialReadStatus.Invalid; // the file was held open, say
        await Assert.ThrowsAsync<AuthenticationRequiredException>(() => provider.GetSnapshotAsync(default));
        source.Status = CredentialReadStatus.Available;

        // Same token, still inside the cooldown: no network, and the last value is still there.
        var served = await provider.GetSnapshotAsync(default);
        Assert.Equal(2, handler.Calls);
        Assert.NotEmpty(served.Windows);
    }

    [Fact]
    public async Task ANewTokenAfterAnInvalidReadStillClearsTheCooldownAndCache()
    {
        var (provider, source, handler, time) = ThrottledProvider();
        await provider.GetSnapshotAsync(default);
        time.Advance(TimeSpan.FromMinutes(3));
        await provider.GetSnapshotAsync(default); // 429 → cooldown

        source.Status = CredentialReadStatus.Invalid;
        await Assert.ThrowsAsync<AuthenticationRequiredException>(() => provider.GetSnapshotAsync(default));
        source.Status = CredentialReadStatus.Available;
        source.Token = "token-two";

        await provider.GetSnapshotAsync(default); // new account must not inherit the block
        Assert.Equal(3, handler.Calls);
    }

    [Fact]
    public async Task ASignOutStillClearsTheCooldownAndCache()
    {
        var (provider, source, handler, time) = ThrottledProvider();
        await provider.GetSnapshotAsync(default);
        time.Advance(TimeSpan.FromMinutes(3));
        await provider.GetSnapshotAsync(default); // 429 → cooldown

        source.Status = CredentialReadStatus.Missing;
        Assert.Empty((await provider.GetSnapshotAsync(default)).Windows);
        source.Status = CredentialReadStatus.Available;

        await provider.GetSnapshotAsync(default); // signing back in starts clean
        Assert.Equal(3, handler.Calls);
    }

    private static (CodexProvider Provider, MutableSource Source, SequenceHandler Handler, MutableTime Time) ThrottledProvider()
    {
        var handler = new SequenceHandler(Ok(), TooMany(), Ok());
        var time = new MutableTime();
        var source = new MutableSource("token-one");
        return (new CodexProvider(new HttpClient(handler), source, time: time), source, handler, time);
    }

    private static (CodexProvider Provider, MutableTime Time) Provider(SequenceHandler handler)
    {
        var time = new MutableTime();
        var provider = new CodexProvider(new HttpClient(handler), new MutableSource("token"), time: time);
        return (provider, time);
    }

    private sealed record Response(string Json, HttpStatusCode Status, int RetryAfterSeconds = 0);

    private static Response Ok()
        => new("""{ "rate_limit": { "primary_window": { "used_percent": 42 } } }""", HttpStatusCode.OK);

    private static Response TooMany(int retryAfterSeconds = 0)
        => new("{}", HttpStatusCode.TooManyRequests, retryAfterSeconds);

    private static Response NotFound() => new("{}", HttpStatusCode.NotFound);

    private static Response Unauthorized() => new("{}", HttpStatusCode.Unauthorized);

    private sealed class MutableTime : TimeProvider
    {
        private DateTimeOffset _utcNow = new(2026, 8, 3, 9, 0, 0, TimeSpan.Zero);
        private long _timestamp = TimeSpan.TicksPerHour;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override DateTimeOffset GetUtcNow() => _utcNow;
        public override long GetTimestamp() => _timestamp;

        public void Advance(TimeSpan elapsed)
        {
            _utcNow += elapsed;
            _timestamp += elapsed.Ticks;
        }
    }

    private sealed class FakeRefresher : IDelegatedTokenRefresher
    {
        public Task<bool> TryRefreshAsync(CancellationToken cancellationToken) => Task.FromResult(true);
    }

    private sealed class MutableSource(string token) : ICredentialSource
    {
        public string Token { get; set; } = token;
        public CredentialReadStatus Status { get; set; } = CredentialReadStatus.Available;
        public CredentialOwner Owner => CredentialOwner.CliLocal;
        public CredentialSource Source => CredentialSource.CliLocal;
        public Task<CredentialReadResult> ReadAsync(ToolKind tool, CancellationToken cancellationToken = default)
            => Task.FromResult(new CredentialReadResult
            {
                Tool = ToolKind.Codex, Status = Status,
                // As the real source: only an Available read carries a credential.
                Credential = Status != CredentialReadStatus.Available ? null : new ToolCredential
                {
                    Tool = ToolKind.Codex, Owner = CredentialOwner.CliLocal, Source = CredentialSource.CliLocal,
                    AccessToken = Token,
                },
            });
    }

    private sealed class SequenceHandler(params Response[] responses) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var (json, status, retryAfter) = responses[Math.Min(Calls, responses.Length - 1)];
            Calls++;
            var response = new HttpResponseMessage(status)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
            if (retryAfter > 0)
            {
                response.Headers.TryAddWithoutValidation("Retry-After", retryAfter.ToString());
            }
            return Task.FromResult(response);
        }
    }
}
