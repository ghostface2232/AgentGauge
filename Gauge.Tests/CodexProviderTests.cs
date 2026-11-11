using System.Net;
using System.Diagnostics;
using System.Globalization;
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
    public async Task ManualRefreshCanReadAppServerAfterAnUnusableHttpResponse()
    {
        var directory = Directory.CreateTempSubdirectory("Gauge-AppServer-Test");
        try
        {
            var executable = Path.Combine(directory.FullName, "codex.cmd");
            await File.WriteAllTextAsync(executable, """
                @echo off
                "%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -NonInteractive -Command "$null = [Console]::ReadLine(); $null = [Console]::ReadLine(); if (([Console]::ReadLine() | ConvertFrom-Json).method -ne 'account/rateLimits/read') { exit 1 }"
                if errorlevel 1 exit /b 1
                echo {"id":1,"result":{}}
                echo {"method":"account/rateLimits/updated"}
                echo {"id":2,"result":{"accountId":"test-account","rateLimits":{"planType":"plus","primary":{"usedPercent":17,"windowDurationMins":10080}}}}
                """);
            const string partial = """
                {"rate_limit":{"primary_window":{"limit_window_seconds":604800}},
                 "additional_rate_limits":[{"limit_name":"GPT Reserve",
                   "rate_limit":{"primary_window":{"used_percent":30,"limit_window_seconds":18000}}}]}
                """;
            var provider = new CodexProvider(new HttpClient(new StubHandler(partial)), Available("t", "test-account"),
                appServerLocator: new AppServerLocator(executable));
            await Assert.ThrowsAsync<UsageResponseShapeException>(() => provider.GetSnapshotAsync(default));

            var snapshot = await provider.GetSnapshotAsync(FetchInteraction.UserInitiated, default);
            Assert.Equal("Plus", snapshot.Plan);
            var window = Assert.Single(snapshot.Windows);
            Assert.Equal(UsageWindowType.Weekly, window.Type);
            Assert.Equal(0.17, window.UsedRatio, 4);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Theory]
    [InlineData("""{"rate_limit":"unexpected"}""")]
    [InlineData("""{"rate_limit":{"secondary_window":[]}}""")]
    [InlineData("""{"rate_limit":null,"additional_rate_limits":{}}""")]
    public async Task RejectsRecognizedFieldsWithUnexpectedTypes(string json)
        => await Assert.ThrowsAsync<UsageResponseShapeException>(() => Snapshot(json, Available("t")));

    [Fact]
    public void NamedOnlyAppServerQuotaIsNotDuplicatedAsAccountWide()
    {
        const string reply = """
        {"id":2,"result":{
          "rateLimits":{"limitId":"gpt_reserve","planType":"pro","primary":{"usedPercent":45,"windowDurationMins":300}},
          "rateLimitsByLimitId":{"gpt_reserve":{"limitName":"GPT Reserve","primary":{"usedPercent":45,"windowDurationMins":300}}}
        }}
        """;
        var payload = CodexProvider.ParseAppServer(reply);
        Assert.NotNull(payload);
        Assert.Equal("Pro 200", payload.Value.Plan);
        var window = Assert.Single(payload.Value.Windows);
        Assert.Equal("codex-additional-gpt-reserve-18000", window.Key);
        Assert.Equal("Luna Reserve", window.GroupLabel);
    }

    [Fact]
    public void AppServerRejectsUnreadableAccountWindowEvenWithNamedLimit()
    {
        const string reply = """
        {"id":2,"result":{"rateLimitsByLimitId":{
          "codex":{"primary":{"windowDurationMins":10080}},
          "gpt_reserve":{"primary":{"usedPercent":45,"windowDurationMins":300}}
        }}}
        """;
        Assert.Null(CodexProvider.ParseAppServer(reply));
    }

    private sealed class AppServerLocator(string executable) : ICliLocator
    {
        public string? Find(string commandName) => commandName == "codex" ? executable : null;
    }

    [Fact]
    public async Task UsageProbeCleansUpChildrenAfterSuccessfulRootExit()
    {
        var executable = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        var startedAt = DateTime.Now;
        var output = await CliUsageProbe.RunAsync(executable, """
            -NoProfile -NonInteractive -Command "$info = New-Object System.Diagnostics.ProcessStartInfo; $info.FileName = Join-Path $PSHOME 'powershell.exe'; $info.Arguments = '-NoProfile -NonInteractive -Command Start-Sleep -Seconds 30'; $info.UseShellExecute = $false; $info.CreateNoWindow = $true; $child = [Diagnostics.Process]::Start($info); [Console]::WriteLine($child.Id)"
            """, TimeSpan.FromSeconds(10), default);
        Assert.NotNull(output);
        var pid = int.Parse(output.Trim(), CultureInfo.InvariantCulture);
        Process child;
        try { child = Process.GetProcessById(pid); }
        catch (ArgumentException) { return; }
        using (child)
        {
            if (child.HasExited || child.StartTime < startedAt || child.ProcessName != "powershell") return;
            try
            {
                await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2));
            }
            finally
            {
                if (!child.HasExited) child.Kill();
            }
        }
    }

    [Fact]
    public void AppServerResponseUsesDurationsAndKeepsNamedLimits()
    {
        const string reply = """
        {"id":2,"result":{"accountId":"test-account",
          "rateLimits":{"limitId":"codex","planType":"pro","primary":{
            "usedPercent":29,"windowDurationMins":10080,"resetsAt":1800000000}},
          "rateLimitResetCredits":{"availableCount":1,"credits":[{"status":"available","expiresAt":1800000100}]},
          "rateLimitsByLimitId":{
            "codex":{"primary":{"usedPercent":29,"windowDurationMins":10080}},
            "gpt_reserve":{"limitName":"GPT Reserve","primary":{"usedPercent":45,"windowDurationMins":300}},
            "unknown":{"primary":{"usedPercent":45,"windowDurationMins":60}}
          }
        }}
        """;
        var payload = CodexProvider.ParseAppServer(reply, "test-account");
        Assert.NotNull(payload);
        var (plan, windows, credits, deadlines) = payload.Value;
        Assert.Equal("Pro 200", plan);
        Assert.Equal(UsageWindowType.Weekly, windows[0].Type);
        Assert.Equal("codex-additional-gpt-reserve-18000", windows[1].Key);
        Assert.Equal("Luna Reserve", windows[1].GroupLabel);
        Assert.Equal(2, windows.Count);
        Assert.Equal(1, credits);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1800000100), Assert.Single(deadlines!).ExpiresAt);
        Assert.Null(CodexProvider.ParseAppServer(reply, "another-account"));
        Assert.Null(CodexProvider.ParseAppServer("""{"id":2,"error":{"code":-32603}}"""));
    }
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

        Assert.Equal("Pro 200", snapshot.Plan);
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
    [InlineData("""{ "rate_limit": {} }""")]
    [InlineData("""{ "rate_limit": null }""")]
    [InlineData("""{ "rate_limit": { "primary_window": null, "secondary_window": null } }""")]
    public async Task NoWindowsReportedIsALegitimateEmptyResult(string json)
    {
        var snapshot = await Snapshot(json, Available("t"));
        Assert.Empty(snapshot.Windows);
    }

    [Theory]
    [InlineData("{}")]                                                            // no rate_limit
    [InlineData("""{ "plan_type": "plus" }""")]                                   // rate_limit renamed
    [InlineData("""{ "rate_limit": { "primary_window": { "reset_at": 1 } } }""")] // used_percent absent
    [InlineData("""{ "rate_limit": { "primary_window": { "used_percent": "x" } } }""")] // wrong type
    [InlineData("""{ "rate_limit": { "primary_window": { "used_percent": 5, "limit_window_seconds": 2592000 } } }""")] // unknown duration
    [InlineData("""{"rate_limit":{"primary_window":{"used_percent":5,"limit_window_seconds":18000},"secondary_window":{"used_percent":25,"limit_window_seconds":3600}}}""")]
    public async Task AReportedAccountWindowThatCannotBeReadFailsTheRefresh(string json)
        => await Assert.ThrowsAsync<UsageResponseShapeException>(() => Snapshot(json, Available("t")));

    [Fact]
    public async Task UsedPercentIsClamped()
    {
        var snapshot = await Snapshot(
            """{ "rate_limit": { "primary_window": { "used_percent": 250 } } }""", Available("t"));
        var window = Assert.Single(snapshot.Windows);
        Assert.Equal(1.0, window.UsedRatio, 3);
    }

    [Theory]
    [InlineData("prolite", "Pro 100")]
    [InlineData("pro", "Pro 200")]
    [InlineData("promax", "Pro 500")]
    [InlineData("PROMAX", "Pro 500")]
    [InlineData("plus", "Plus")]
    public async Task MapsProTiersToMarketedNames(string planType, string expected)
    {
        var snapshot = await Snapshot($$"""{ "plan_type": "{{planType}}", "rate_limit": {} }""", Available("t"));

        Assert.Equal(expected, snapshot.Plan);
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
    public async Task ReadsHeldResetCreditsRatherThanTheCurrentlyApplicableOnes()
    {
        // applicable_available_count is the subset spendable against the limit in force, so it
        // can differ from the balance needed to detect a manual reset.
        const string json = """
        {
          "plan_type": "plus",
          "rate_limit": { "primary_window": { "used_percent": 3, "limit_window_seconds": 18000 } },
          "rate_limit_reset_credits": { "available_count": 3, "applicable_available_count": 0 }
        }
        """;

        var snapshot = await Snapshot(json, Available("t"));

        Assert.Equal(3, snapshot.ResetCredits);
    }

    [Fact]
    public async Task ReadsOnlyAvailableDatedCreditsAndReusesTheSupplementalRead()
    {
        const string usage = """
        { "rate_limit": { "primary_window": { "used_percent": 3, "limit_window_seconds": 18000 } },
          "rate_limit_reset_credits": { "available_count": 2 } }
        """;
        const string credits = """
        { "credits": [
          {"status":"available","expires_at":"2027-03-09T07:59:00Z"},
          {"status":"redeemed","expires_at":"2027-03-08T07:59:00Z"},
          {"status":"available","expires_at":null}
        ] }
        """;
        var handler = new StubHandler(usage, resetDetails: credits);
        var provider = new CodexProvider(new HttpClient(handler), Available("t"));
        var first = await provider.GetSnapshotAsync(default);
        var second = await provider.GetSnapshotAsync(default);

        var deadline = Assert.Single(first.ResetCreditDeadlines!);
        Assert.Equal(new DateTimeOffset(2027, 3, 9, 7, 59, 0, TimeSpan.Zero), deadline.ExpiresAt);
        Assert.Equal(deadline, Assert.Single(second.ResetCreditDeadlines!));
        Assert.Equal(1, handler.ResetReads);
    }

    [Fact]
    public async Task ReadsDeadlinesAgainAfterChangingLoginInTheSameWorkspace()
    {
        const string usage = """
        { "rate_limit": {}, "rate_limit_reset_credits": { "available_count": 1 } }
        """;
        var handler = new StubHandler(usage, resetDetails: """{ "credits": [] }""");
        var credential = new ToolCredential
        {
            Tool = ToolKind.Codex, Owner = CredentialOwner.CliLocal, Source = CredentialSource.CliLocal,
            AccountId = "workspace", AccessToken = "alice-token", HistoryIdentity = "alice",
        };
        var source = new StubSource(new CredentialReadResult
        {
            Tool = ToolKind.Codex, Status = CredentialReadStatus.Available, Credential = credential,
        });
        var provider = new CodexProvider(new HttpClient(handler), source);
        await provider.GetSnapshotAsync(default);

        source.Result = source.Result with { Credential = credential with { AccessToken = "rotated-token" } };
        await provider.GetSnapshotAsync(default);
        Assert.Equal(1, handler.ResetReads);

        source.Result = source.Result with { Credential = credential with { AccessToken = "bob-token", HistoryIdentity = "bob" } };
        await provider.GetSnapshotAsync(default);
        Assert.Equal(2, handler.ResetReads);
    }

    [Theory]
    [InlineData("""{ "rate_limit": {}, "rate_limit_reset_credits": { "available_count": 0 } }""", 0)]
    [InlineData("""{ "rate_limit": {}, "rate_limit_reset_credits": {} }""", null)]
    [InlineData("""{ "rate_limit": {}, "rate_limit_reset_credits": null }""", null)]
    [InlineData("""{ "rate_limit": {} }""", null)]
    [InlineData("""{ "rate_limit": {}, "rate_limit_reset_credits": { "available_count": "2" } }""", null)]
    public async Task MissingOrMistypedResetCreditsStayAbsentInsteadOfBecomingZero(string json, int? expected)
    {
        // A plan without the feature must not be reported as a spent balance, so only a real
        // number becomes a count; everything else stays null.
        var snapshot = await Snapshot(json, Available("t"));

        Assert.Equal(expected, snapshot.ResetCredits);
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
        public CredentialReadResult Result { get; set; } = result;
        public CredentialOwner Owner => CredentialOwner.CliLocal;
        public CredentialSource Source => CredentialSource.CliLocal;
        public Task<CredentialReadResult> ReadAsync(ToolKind tool, CancellationToken cancellationToken = default)
            => Task.FromResult(Result);
    }

    private sealed class StubHandler(
        string json, HttpStatusCode statusCode = HttpStatusCode.OK, string? resetDetails = null) : HttpMessageHandler
    {
        public int ResetReads { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var isCreditRequest = request.RequestUri?.AbsolutePath == "/backend-api/wham/rate-limit-reset-credits";
            if (isCreditRequest) ResetReads++;
            return Task.FromResult(new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(isCreditRequest ? resetDetails ?? "{}" : json, Encoding.UTF8, "application/json"),
            });
        }
    }
}
