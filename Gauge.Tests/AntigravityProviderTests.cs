using Gauge.Models;
using Gauge.Providers;
using Gauge.Providers.Internal;

namespace Gauge.Tests;

/// <summary>
/// Orchestration for <see cref="AntigravityProvider"/>: attach-first then delegate, the
/// not-installed empty result, and the failure semantics that preserve the coordinator's cache.
/// Plan parsing and richest-response selection are covered alongside.
/// </summary>
public sealed class AntigravityProviderTests
{
    private const string FourWindows = """
    { "response": { "groups": [
        { "buckets": [
            { "bucketId": "gemini-weekly", "window": "weekly", "remainingFraction": 0.99 },
            { "bucketId": "gemini-5h", "window": "5h", "remainingFraction": 0.85 } ] },
        { "buckets": [
            { "bucketId": "3p-weekly", "window": "weekly", "remainingFraction": 1 },
            { "bucketId": "3p-5h", "window": "5h", "remainingFraction": 1 } ] } ] } }
    """;

    private const string OneWindow = """
    { "response": { "groups": [ { "buckets": [
        { "bucketId": "gemini-5h", "window": "5h", "remainingFraction": 0.5 } ] } ] } }
    """;

    [Fact]
    public void ReadsOnlyTheAgyUsageCommandReport()
    {
        const string json = """
        {"status":"SUCCESS","num_turns":0,"command":{"name":"usage","data":{"groups":[
          {"name":"Gemini Models","buckets":[
            {"id":"gemini-weekly","window":"weekly","remaining_fraction":0.86,"reset_time":"2026-12-01T08:00:00Z"},
            {"id":"gemini-weekly","window":"weekly","remaining_fraction":0.92},
            {"id":"gemini-5h","window":"5h"}]},
          {"name":"Claude and GPT models","buckets":[
            {"id":"3p-5h","window":"5h","remaining_fraction":1}]}
        ]}}}
        """;
        var windows = AntigravityQuotaParser.Parse(json);
        Assert.Equal(new[] { "gemini-weekly", "3p-5h" }, windows.Select(window => window.Key));
        Assert.Equal("Claude/GPT", windows[1].GroupLabel);
        Assert.Equal(0.14, windows[0].UsedRatio, 4);
        Assert.Equal(new DateTimeOffset(2026, 12, 1, 8, 0, 0, TimeSpan.Zero), windows[0].ResetTime);
        Assert.Empty(AntigravityQuotaParser.Parse(json.Replace("\"num_turns\":0", "\"num_turns\":1")));
    }

    [Fact]
    public async Task NotInstalled_ReturnsEmptySnapshotWithoutReading()
    {
        var attach = new FakeReader(new AntigravityReading(FourWindows, "Pro"));
        var provider = new AntigravityProvider(new IAntigravityReader[] { attach }, isInstalled: () => false);

        var snapshot = await provider.GetSnapshotAsync(default);

        Assert.Empty(snapshot.Windows);
        Assert.Null(snapshot.Plan);
        Assert.False(attach.WasCalled); // not installed short-circuits before any read
    }

    [Fact]
    public async Task Attach_ProducesWindowsAndPlan_WithoutFallingBackToDelegate()
    {
        var attach = new FakeReader(new AntigravityReading(FourWindows, "Pro"));
        var delegateReader = new FakeReader(null);
        var provider = new AntigravityProvider(new IAntigravityReader[] { attach, delegateReader }, () => true);

        var snapshot = await provider.GetSnapshotAsync(default);

        Assert.Equal(4, snapshot.Windows.Count);
        Assert.Equal("Pro", snapshot.Plan);
        Assert.Equal("Antigravity", snapshot.ToolName);
        Assert.Equal(new[] { "gemini-weekly", "gemini-5h", "3p-weekly", "3p-5h" }, snapshot.Windows.Select(w => w.Id));
        Assert.False(delegateReader.WasCalled); // attach succeeded, so delegate is never tried
    }

    [Fact]
    public async Task FallsBackToDelegateWhenAttachFindsNothing()
    {
        var attach = new FakeReader(null);
        var delegateReader = new FakeReader(new AntigravityReading(OneWindow, null));
        var provider = new AntigravityProvider(new IAntigravityReader[] { attach, delegateReader }, () => true);

        var snapshot = await provider.GetSnapshotAsync(default);

        Assert.True(attach.WasCalled);
        Assert.True(delegateReader.WasCalled);
        var window = Assert.Single(snapshot.Windows);
        Assert.Equal("gemini-5h", window.Id);
    }

    [Fact]
    public async Task InstalledButNoEngineReachable_ThrowsToPreserveCache()
    {
        var provider = new AntigravityProvider(
            new IAntigravityReader[] { new FakeReader(null), new FakeReader(null) }, () => true);

        await Assert.ThrowsAsync<AntigravityUnavailableException>(() => provider.GetSnapshotAsync(default));
    }

    [Fact]
    public async Task AvailabilityOnlyResponseWithNoWindows_ThrowsRatherThanClobberCache()
    {
        var reading = new AntigravityReading("""{ "response": { "groups": [] } }""", "Pro");
        var provider = new AntigravityProvider(new IAntigravityReader[] { new FakeReader(reading) }, () => true);

        await Assert.ThrowsAsync<AntigravityUnavailableException>(() => provider.GetSnapshotAsync(default));
    }

    [Fact]
    public void PickRichest_KeepsTheResponseWithMostWindows()
    {
        var richest = new AntigravityReading(FourWindows, null);
        var readings = new[]
        {
            new AntigravityReading(OneWindow, null),
            richest,
            new AntigravityReading("""{ "response": { "groups": [] } }""", null),
        };

        Assert.Same(richest, AntigravityAttachReader.PickRichest(readings));
    }

    [Fact]
    public void PickRichest_ReturnsNullForNoReadings()
    {
        Assert.Null(AntigravityAttachReader.PickRichest(Array.Empty<AntigravityReading>()));
    }

    [Theory]
    // Real shape: plan name nested under userStatus.planStatus.planInfo (no response wrapper).
    [InlineData("""{ "userStatus": { "planStatus": { "planInfo": { "planName": "Pro" } } } }""", "Pro")]
    // Falls back to the human tier name when planName is absent.
    [InlineData("""{ "userStatus": { "userTier": { "name": "Google AI Pro" } } }""", "Google AI Pro")]
    // planName wins over the tier name when both are present.
    [InlineData("""{ "userStatus": { "planStatus": { "planInfo": { "planName": "Pro" } }, "userTier": { "name": "Google AI Pro" } } }""", "Pro")]
    [InlineData("""{ "userStatus": { } }""", null)] // neither present
    [InlineData("""{ }""", null)]                    // no userStatus
    [InlineData("not json", null)]
    public void ParsesPlanFromUserStatus(string json, string? expected)
    {
        Assert.Equal(expected, AntigravityUserStatus.ParsePlan(json));
    }

    private sealed class FakeReader : IAntigravityReader
    {
        private readonly AntigravityReading? _reading;

        public FakeReader(AntigravityReading? reading) => _reading = reading;

        public bool WasCalled { get; private set; }

        public Task<AntigravityReading?> ReadAsync(CancellationToken cancellationToken)
        {
            WasCalled = true;
            return Task.FromResult(_reading);
        }
    }
}
