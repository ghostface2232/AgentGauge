using System.Net;
using System.Text;
using Gauge.Models;
using Gauge.Providers;
using Gauge.Services;

namespace Gauge.Tests;

/// <summary>
/// JSON-schema tolerance, plan mapping, and credential/auth handling for
/// <see cref="CodexProvider"/>. Codex hits the same endpoint the CLI polls every 60s, so
/// it has no throttle/cache of its own; network/API failures must propagate rather than
/// collapse into an empty success (so the coordinator keeps the last good snapshot).
/// </summary>
public sealed class CodexProviderTests
{
    [Fact]
    public async Task ParsesPrimaryAndSecondaryWindows()
    {
        const string json = """
        {
          "plan_type": "pro",
          "rate_limit": {
            "primary_window": { "used_percent": 50, "reset_at": 1790000000 },
            "secondary_window": { "used_percent": 12 }
          }
        }
        """;
        var snapshot = await Snapshot(json, Available("codex-token", "acct"));

        Assert.Equal("Pro", snapshot.Plan);
        Assert.Equal(2, snapshot.Windows.Count);

        var fiveHour = Assert.Single(snapshot.Windows, w => w.Type == UsageWindowType.FiveHour);
        Assert.Equal(0.50, fiveHour.UsedRatio, 3);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1790000000), fiveHour.ResetTime);
        Assert.Null(fiveHour.Duration);

        var weekly = Assert.Single(snapshot.Windows, w => w.Type == UsageWindowType.Weekly);
        Assert.Null(weekly.ResetTime);
        Assert.Null(weekly.Duration);
    }

    [Fact]
    public async Task WeeklyOnlyPrimaryIsClassifiedByDuration()
    {
        const string json = """
        {
          "plan_type": "plus",
          "rate_limit": {
            "primary_window": {
              "used_percent": 27,
              "reset_at": 1790000000,
              "limit_window_seconds": 604800
            },
            "secondary_window": null
          }
        }
        """;

        var snapshot = await Snapshot(json, Available("t"));

        var weekly = Assert.Single(snapshot.Windows);
        Assert.Equal(UsageWindowType.Weekly, weekly.Type);
        Assert.Equal(TimeSpan.FromDays(7), weekly.Duration);
        Assert.Equal("Weekly", weekly.Key);
    }

    [Fact]
    public async Task WindowPositionsMaySwapWithoutChangingTheirTypes()
    {
        const string json = """
        {
          "rate_limit": {
            "primary_window": { "used_percent": 20, "limit_window_seconds": 604800 },
            "secondary_window": { "used_percent": 40, "limit_window_seconds": 18000 }
          }
        }
        """;

        var snapshot = await Snapshot(json, Available("t"));

        Assert.Equal(UsageWindowType.Weekly, snapshot.Windows[0].Type);
        Assert.Equal(UsageWindowType.FiveHour, snapshot.Windows[1].Type);
    }

    [Theory]
    [InlineData("86400")]
    [InlineData("0")]
    [InlineData("\"18000\"")]
    [InlineData("null")]
    public async Task ExplicitUnsupportedOrMalformedDurationIsSkipped(string duration)
    {
        var json = $$"""
        {
          "rate_limit": {
            "primary_window": {
              "used_percent": 20,
              "limit_window_seconds": {{duration}}
            },
            "secondary_window": {
              "used_percent": 40,
              "limit_window_seconds": 604800
            }
          }
        }
        """;

        var snapshot = await Snapshot(json, Available("t"));

        var weekly = Assert.Single(snapshot.Windows);
        Assert.Equal(UsageWindowType.Weekly, weekly.Type);
    }

    [Fact]
    public async Task NamedAdditionalRateLimitsAreAddedLossily()
    {
        const string json = """
        {
          "rate_limit": {
            "primary_window": { "used_percent": 10, "limit_window_seconds": 604800 }
          },
          "additional_rate_limits": [
            {},
            {
              "limit_name": "Fast model",
              "metered_feature": "fast-model",
              "rate_limit": {
                "primary_window": { "used_percent": 25, "limit_window_seconds": 18000 }
              }
            }
          ]
        }
        """;

        var snapshot = await Snapshot(json, Available("t"));

        Assert.Equal(2, snapshot.Windows.Count);
        var additional = Assert.Single(snapshot.Windows, w => w.GroupLabel == "Fast model");
        Assert.Equal("codex-additional-fast-model-18000", additional.Id);
        Assert.Equal(UsageWindowType.FiveHour, additional.Type);
    }

    [Fact]
    public async Task GptReserveUsesTheUserFacingLunaReserveNameWithoutChangingItsIdentity()
    {
        const string json = """
        {
          "additional_rate_limits": [
            {
              "limit_name": "gpt-reserve",
              "metered_feature": "gpt-reserve",
              "rate_limit": {
                "primary_window": { "used_percent": 8, "limit_window_seconds": 604800 }
              }
            }
          ]
        }
        """;

        var snapshot = await Snapshot(json, Available("t"));

        var reserve = Assert.Single(snapshot.Windows);
        Assert.Equal("Luna Reserve", reserve.GroupLabel);
        Assert.Equal("codex-additional-gpt-reserve-604800", reserve.Id);
    }

    [Theory]
    [InlineData("plus", "Plus")]
    [InlineData("go", "Go")]
    [InlineData("business", "Business")]
    [InlineData("startup", "Startup")] // unknown → title-cased
    [InlineData("", null)]
    public async Task MapsPlanType(string planType, string? expected)
    {
        var json = $$"""{ "plan_type": "{{planType}}", "rate_limit": {} }""";
        var snapshot = await Snapshot(json, Available("t"));
        Assert.Equal(expected, snapshot.Plan);
    }

    [Theory]
    [InlineData("{}")]                                                            // no rate_limit
    [InlineData("""{ "rate_limit": { "primary_window": { "reset_at": 1 } } }""")] // used_percent absent
    [InlineData("""{ "rate_limit": { "primary_window": { "used_percent": "x" } } }""")] // wrong type
    public async Task TolerantOfSchemaDrift(string json)
    {
        var snapshot = await Snapshot(json, Available("t"));
        Assert.Empty(snapshot.Windows);
    }

    [Fact]
    public async Task UsedPercentIsClamped()
    {
        var snapshot = await Snapshot(
            """{ "rate_limit": { "primary_window": { "used_percent": 250 } } }""", Available("t"));
        var window = Assert.Single(snapshot.Windows);
        Assert.Equal(1.0, window.UsedRatio, 3);
    }

    [Fact]
    public async Task MissingTokenYieldsEmptySnapshot()
    {
        var provider = new CodexProvider(new HttpClient(new StubHandler("{}")), Missing());
        var snapshot = await provider.GetSnapshotAsync(default);
        Assert.Empty(snapshot.Windows);
        Assert.Null(snapshot.Plan);
    }

    [Fact]
    public async Task InvalidCredentialBecomesAuthenticationRequired()
    {
        var provider = new CodexProvider(new HttpClient(new StubHandler("{}")), Invalid());
        var error = await Assert.ThrowsAsync<AuthenticationRequiredException>(
            () => provider.GetSnapshotAsync(default));
        Assert.Equal(ToolKind.Codex, error.Tool);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task UnauthorizedResponseBecomesAuthenticationRequired(HttpStatusCode code)
    {
        var provider = new CodexProvider(new HttpClient(new StubHandler("{}", code)), Available("t"));
        var error = await Assert.ThrowsAsync<AuthenticationRequiredException>(
            () => provider.GetSnapshotAsync(default));
        Assert.Equal(ToolKind.Codex, error.Tool);
    }

    [Fact]
    public async Task ServerErrorPropagatesInsteadOfEmptySuccess()
    {
        var provider = new CodexProvider(
            new HttpClient(new StubHandler("{}", HttpStatusCode.InternalServerError)), Available("t"));
        await Assert.ThrowsAnyAsync<HttpRequestException>(() => provider.GetSnapshotAsync(default));
    }

    private static async Task<UsageSnapshot> Snapshot(string json, ICredentialSource source)
    {
        var provider = new CodexProvider(new HttpClient(new StubHandler(json)), source);
        return await provider.GetSnapshotAsync(default);
    }

    private static ICredentialSource Available(string token, string? accountId = null) => new StubSource(
        new CredentialReadResult
        {
            Tool = ToolKind.Codex, Status = CredentialReadStatus.Available,
            Credential = new ToolCredential
            {
                Tool = ToolKind.Codex, Owner = CredentialOwner.CliLocal, Source = CredentialSource.CliLocal,
                AccessToken = token, AccountId = accountId,
            },
        });

    private static ICredentialSource Missing() => new StubSource(
        new CredentialReadResult { Tool = ToolKind.Codex, Status = CredentialReadStatus.Missing });

    private static ICredentialSource Invalid() => new StubSource(
        new CredentialReadResult { Tool = ToolKind.Codex, Status = CredentialReadStatus.Invalid });

    private sealed class StubSource(CredentialReadResult result) : ICredentialSource
    {
        public CredentialOwner Owner => CredentialOwner.CliLocal;
        public CredentialSource Source => CredentialSource.CliLocal;
        public Task<CredentialReadResult> ReadAsync(ToolKind tool, CancellationToken cancellationToken = default)
            => Task.FromResult(result);
    }

    private sealed class StubHandler(string json, HttpStatusCode statusCode = HttpStatusCode.OK) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            });
    }
}
