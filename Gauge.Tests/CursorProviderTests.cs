using System.Net;
using System.Text;
using Gauge.Models;
using Gauge.Providers;
using Gauge.Services;

namespace Gauge.Tests;

public sealed class CursorProviderTests
{
    [Fact]
    public async Task ParsesTotalPercentIntoSingleBillingCycleWindow()
    {
        const string json = """
        {
          "membershipType": "pro",
          "billingCycleEnd": "2026-07-01T00:00:00Z",
          "individualUsage": { "plan": { "used": 2000, "limit": 2000, "totalPercentUsed": 36.0 } }
        }
        """;
        var handler = new StubHandler(json);
        var provider = new CursorProvider(new HttpClient(handler), CursorSource("jwt-token", "user_abc"));

        var snapshot = await provider.GetSnapshotAsync(default);

        Assert.Equal("Pro", snapshot.Plan);
        var window = Assert.Single(snapshot.Windows);
        Assert.Equal(UsageWindowType.BillingCycle, window.Type);
        Assert.Equal(0.36, window.UsedRatio, 3);
        Assert.NotNull(window.ResetTime);
        // Cookie is built from userId + token.
        Assert.Equal("WorkosCursorSessionToken=user_abc%3A%3Ajwt-token", handler.LastCookie);
    }

    [Fact]
    public async Task FallsBackToUsedOverLimitWhenNoTotalPercent()
    {
        const string json = """
        { "individualUsage": { "plan": { "used": 1500, "limit": 2000 } } }
        """;
        var provider = new CursorProvider(new HttpClient(new StubHandler(json)), CursorSource("t", "u"));

        var snapshot = await provider.GetSnapshotAsync(default);

        var window = Assert.Single(snapshot.Windows);
        Assert.Equal(0.75, window.UsedRatio, 3);
    }

    [Fact]
    public async Task NotLoggedInYieldsEmptySnapshot()
    {
        var provider = new CursorProvider(new HttpClient(new StubHandler("{}")), MissingSource());
        var snapshot = await provider.GetSnapshotAsync(default);
        Assert.Empty(snapshot.Windows);
    }

    [Fact]
    public async Task UnauthorizedBecomesAuthenticationRequired()
    {
        var handler = new StubHandler("{}", HttpStatusCode.Unauthorized);
        var provider = new CursorProvider(new HttpClient(handler), CursorSource("t", "u"));
        var error = await Assert.ThrowsAsync<AuthenticationRequiredException>(() => provider.GetSnapshotAsync(default));
        Assert.Equal(ToolKind.Cursor, error.Tool);
    }

    [Fact]
    public async Task AveragesAutoAndApiWhenNoTotalPercent()
    {
        const string json = """
        { "individualUsage": { "plan": { "autoPercentUsed": 40, "apiPercentUsed": 60 } } }
        """;
        var ratio = await Ratio(json);
        Assert.Equal(0.50, ratio, 3);
    }

    [Theory]
    [InlineData("""{ "individualUsage": { "plan": { "apiPercentUsed": 30 } } }""", 0.30)]
    [InlineData("""{ "individualUsage": { "plan": { "autoPercentUsed": 25 } } }""", 0.25)]
    [InlineData("""{ "individualUsage": { "overall": { "used": 3, "limit": 4 } } }""", 0.75)]
    [InlineData("""{ "teamUsage": { "pooled": { "used": 1, "limit": 5 } } }""", 0.20)]
    public async Task FallsThroughPrecedenceChain(string json, double expected)
    {
        Assert.Equal(expected, await Ratio(json), 3);
    }

    [Fact]
    public async Task PercentAboveHundredIsClamped()
    {
        var ratio = await Ratio("""{ "individualUsage": { "plan": { "totalPercentUsed": 250 } } }""");
        Assert.Equal(1.0, ratio, 3);
    }

    [Theory]
    [InlineData("pro_plus", "Pro+")]
    [InlineData("pro-plus", "Pro+")]
    [InlineData("ultra", "Ultra")]
    [InlineData("business", "Business")]
    [InlineData("hobby", "Hobby")]
    public async Task MapsMembershipType(string membership, string expected)
    {
        var json = $$"""{ "membershipType": "{{membership}}", "individualUsage": { "plan": { "totalPercentUsed": 10 } } }""";
        var provider = new CursorProvider(new HttpClient(new StubHandler(json)), CursorSource("t", "u"));
        var snapshot = await provider.GetSnapshotAsync(default);
        Assert.Equal(expected, snapshot.Plan);
    }

    private static async Task<double> Ratio(string json)
    {
        var provider = new CursorProvider(new HttpClient(new StubHandler(json)), CursorSource("t", "u"));
        var snapshot = await provider.GetSnapshotAsync(default);
        return Assert.Single(snapshot.Windows).UsedRatio;
    }

    private static ICredentialSource CursorSource(string token, string userId) => new StubSource(
        new CredentialReadResult
        {
            Tool = ToolKind.Cursor, Status = CredentialReadStatus.Available,
            Credential = new ToolCredential
            {
                Tool = ToolKind.Cursor, Owner = CredentialOwner.CliLocal, Source = CredentialSource.CliLocal,
                AccessToken = token, AccountId = userId,
            },
        });

    private static ICredentialSource MissingSource() => new StubSource(
        new CredentialReadResult { Tool = ToolKind.Cursor, Status = CredentialReadStatus.Missing });

    private sealed class StubSource(CredentialReadResult result) : ICredentialSource
    {
        public CredentialOwner Owner => CredentialOwner.CliLocal;
        public CredentialSource Source => CredentialSource.CliLocal;
        public Task<CredentialReadResult> ReadAsync(ToolKind tool, CancellationToken cancellationToken = default)
            => Task.FromResult(result);
    }

    private sealed class StubHandler(string json, HttpStatusCode statusCode = HttpStatusCode.OK) : HttpMessageHandler
    {
        public string? LastCookie { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastCookie = request.Headers.TryGetValues("Cookie", out var values) ? string.Join("; ", values) : null;
            return Task.FromResult(new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            });
        }
    }
}
