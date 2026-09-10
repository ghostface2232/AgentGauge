using System.Net;
using System.Net.Http;
using System.Text.Json;
using Gauge.Localization;
using Gauge.Models;
using Gauge.Services;
using Gauge.ViewModels;

namespace Gauge.Tests;

public sealed class ProviderStatusServiceTests
{
    [Theory]
    [InlineData("none", ProviderStatusLevel.Operational)]
    [InlineData("minor", ProviderStatusLevel.Minor)]
    [InlineData("major", ProviderStatusLevel.Major)]
    [InlineData("critical", ProviderStatusLevel.Critical)]
    [InlineData("maintenance", ProviderStatusLevel.Maintenance)]
    public void ParsesObservedStatuspageShape(string indicator, ProviderStatusLevel expected)
    {
        using var json = JsonDocument.Parse(Payload(indicator));
        var result = ProviderStatusService.Parse(ToolKind.Codex, json.RootElement, DateTimeOffset.UtcNow);
        Assert.Equal(expected, result.Level);
        Assert.Equal(expected != ProviderStatusLevel.Operational, ProviderStatusText.IsVisible(result));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"status\":{\"indicator\":\"new-value\"}}")]
    public void MissingOrUnknownStatusNeverMeansOperational(string payload)
    {
        using var json = JsonDocument.Parse(payload);
        Assert.ThrowsAny<Exception>(() => ProviderStatusService.Parse(ToolKind.Codex, json.RootElement, DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task PollsEnabledServicesOnlyRetainsStaleStatusAndClearsOnRecovery()
    {
        var time = new Clock();
        var enabled = new HashSet<ToolKind> { ToolKind.Codex, ToolKind.Cursor, ToolKind.Antigravity };
        var handler = new Handler();
        using var http = new HttpClient(handler);
        using var service = new ProviderStatusService(http, enabled.Contains, time);
        IReadOnlyList<ProviderStatus> statuses = [];
        service.Updated += (_, s) => statuses = s;
        await service.RefreshAsync();
        Assert.Equal(2, handler.Calls);
        var codex = Assert.Single(statuses, s => s.Tool == ToolKind.Codex);
        Assert.Equal(ProviderStatusLevel.Major, codex.Level);
        await service.RefreshAsync();
        Assert.Equal(2, handler.Calls);
        time.Advance(ProviderStatusService.PollInterval);
        handler.FailCodex = true;
        await service.RefreshAsync();
        Assert.Equal(codex with { LastCheckFailed = true }, Assert.Single(statuses, s => s.Tool == ToolKind.Codex));
        Assert.False(Assert.Single(statuses, s => s.Tool == ToolKind.Cursor).LastCheckFailed);
        time.Advance(ProviderStatusService.PollInterval);
        handler.FailCodex = false;
        handler.Indicator = "none";
        await service.RefreshAsync();
        Assert.All(statuses, s => Assert.False(ProviderStatusText.IsVisible(s)));
        enabled.Remove(ToolKind.Codex);
        await service.RefreshAsync();
        Assert.Single(statuses);
        service.Dispose();
        var count = handler.Calls;
        await service.RefreshAsync();
        Assert.Equal(count, handler.Calls);
    }

    [Fact]
    public async Task ColdFailureIsUnknownAndAttemptIsThrottled()
    {
        var handler = new Handler { FailCodex = true };
        using var http = new HttpClient(handler);
        using var service = new ProviderStatusService(http, k => k == ToolKind.Codex);
        ProviderStatus? status = null;
        service.Updated += (_, s) => status = Assert.Single(s);
        await service.RefreshAsync();
        await service.RefreshAsync();
        Assert.Equal(1, handler.Calls);
        Assert.Equal(ProviderStatusLevel.Unknown, status!.Level);
        Assert.Null(status.CheckedAt);
    }

    [Fact]
    public void ServiceHealthSurvivesUsageRefreshAndDoesNotChangeAccountState()
    {
        var vm = new UsageViewModel();
        var status = new ProviderStatus(ToolKind.Codex, ProviderStatusLevel.Major, "Incident", DateTimeOffset.UtcNow);
        vm.ApplyServiceStatuses([status]);
        var cached = new CachedUsage
        {
            ToolName = "Codex", LastRefreshFailed = true,
            Snapshot = new UsageSnapshot { ToolName = "Codex", Windows = [] },
        };
        vm.Apply(new UsageState { Tools = [cached] });
        var card = Assert.Single(vm.Cards);
        Assert.True(card.HasServiceStatus);
        Assert.True(card.HasRefreshIssue);
        Assert.Contains("Codex", vm.TrayStatusSummary);
        vm.ApplyServiceStatuses([status with { Level = ProviderStatusLevel.Operational }]);
        Assert.False(card.HasServiceStatus);
        Assert.True(card.HasRefreshIssue);
        Assert.Empty(vm.TrayStatusSummary);
    }

    private static string Payload(string indicator) => JsonSerializer.Serialize(new
    {
        page = new { id = "public-page", updated_at = "2026-09-10T06:01:47Z" },
        status = new { indicator, description = "Service status" },
    });

    private sealed class Clock : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddTicks(_ticks);
        public void Advance(TimeSpan span) => _ticks += span.Ticks;
    }

    private sealed class Handler : HttpMessageHandler
    {
        public int Calls;
        public bool FailCodex;
        public string Indicator = "major";
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            Assert.Null(request.Headers.Authorization);
            Assert.False(request.Headers.Contains("Cookie"));
            Assert.Equal("/api/v2/status.json", request.RequestUri!.AbsolutePath);
            if (FailCodex && request.RequestUri.Host == "status.openai.com") throw new HttpRequestException("offline");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Payload(Indicator)) });
        }
    }
}
