using Gauge.Models;
using Gauge.Providers.Internal;

namespace Gauge.Tests;

/// <summary>
/// Tolerance and normalization for <see cref="AntigravityQuotaParser"/>. The fixture in
/// <see cref="ParsesFourWindowsFromLiveCapture"/> is the real <c>RetrieveUserQuotaSummary</c>
/// response (account identifiers aside); the remaining cases cover the schema drift the parser
/// must survive without inventing usage values.
/// </summary>
public sealed class AntigravityQuotaParserTests
{
    // The live capture: two families (Gemini, Claude/GPT) × (weekly, 5-hour) = four buckets.
    private const string LiveCapture = """
    {
      "response": {
        "groups": [
          {
            "displayName": "Gemini Models",
            "description": "Models within this group: Gemini Flash, Gemini Pro",
            "buckets": [
              {
                "bucketId": "gemini-weekly",
                "displayName": "Weekly Limit",
                "description": "You have used some of your weekly limit, it will fully refresh in 6 days, 23 hours.",
                "window": "weekly",
                "remainingFraction": 0.9989373,
                "resetTime": "2026-06-28T00:03:00Z"
              },
              {
                "bucketId": "gemini-5h",
                "displayName": "Five Hour Limit",
                "description": "You have used some of your 5-hour limit, it will fully refresh in 4 hours, 57 minutes.",
                "window": "5h",
                "remainingFraction": 0.9936241,
                "resetTime": "2026-06-21T05:03:00Z"
              }
            ]
          },
          {
            "displayName": "Claude and GPT models",
            "description": "Models within this group: Claude Opus, Claude Sonnet, GPT-OSS",
            "buckets": [
              { "bucketId": "3p-weekly", "displayName": "Weekly Limit", "window": "weekly", "remainingFraction": 1, "resetTime": "2026-06-28T00:05:32Z" },
              { "bucketId": "3p-5h",     "displayName": "Five Hour Limit", "window": "5h",   "remainingFraction": 1, "resetTime": "2026-06-21T05:05:32Z" }
            ]
          }
        ],
        "description": "Within each group, models share a weekly limit and a 5-hour limit."
      }
    }
    """;

    [Fact]
    public void ParsesFourWindowsFromLiveCapture()
    {
        var windows = AntigravityQuotaParser.Parse(LiveCapture);

        // Provider order preserved, each bucketId carried through as the stable window Id.
        Assert.Equal(
            new[] { "gemini-weekly", "gemini-5h", "3p-weekly", "3p-5h" },
            windows.Select(w => w.Id));
        // Ids double as reconciliation keys, keeping the two 5-hour windows distinct.
        Assert.Equal(
            new[] { "gemini-weekly", "gemini-5h", "3p-weekly", "3p-5h" },
            windows.Select(w => w.Key));

        var geminiWeekly = windows[0];
        Assert.Equal(UsageWindowType.Weekly, geminiWeekly.Type);
        Assert.Equal(1 - 0.9989373, geminiWeekly.UsedRatio, 6);
        Assert.Equal(DateTimeOffset.Parse("2026-06-28T00:03:00Z"), geminiWeekly.ResetTime);

        var geminiFiveHour = windows[1];
        Assert.Equal(UsageWindowType.FiveHour, geminiFiveHour.Type);
        Assert.Equal(1 - 0.9936241, geminiFiveHour.UsedRatio, 6);

        // remainingFraction == 1 → fully available, never clamped into spent.
        Assert.Equal(0.0, windows[2].UsedRatio, 6);
        Assert.Equal(0.0, windows[3].UsedRatio, 6);
        Assert.All(windows, w => Assert.False(string.IsNullOrEmpty(w.Label)));
    }

    [Fact]
    public void ToleratesSummaryAliasAndRootLevelGroups()
    {
        const string aliased = """{ "summary": { "groups": [ { "buckets": [ { "bucketId": "gemini-5h", "window": "5h", "remainingFraction": 0.5 } ] } ] } }""";
        const string bareRoot = """{ "groups": [ { "buckets": [ { "bucketId": "gemini-5h", "window": "5h", "remainingFraction": 0.5 } ] } ] }""";

        foreach (var json in new[] { aliased, bareRoot })
        {
            var window = Assert.Single(AntigravityQuotaParser.Parse(json));
            Assert.Equal("gemini-5h", window.Id);
            Assert.Equal(0.5, window.UsedRatio, 6);
        }
    }

    [Theory]
    // Wrapped oneof: {case, value}.
    [InlineData("""{ "bucketId": "b", "window": "5h", "remaining": { "case": "remainingFraction", "value": 0.25 } }""", 0.75)]
    // Nested object form.
    [InlineData("""{ "bucketId": "b", "window": "5h", "remaining": { "remainingFraction": 0.25 } }""", 0.75)]
    // Negative fraction clamps to fully spent.
    [InlineData("""{ "bucketId": "b", "window": "5h", "remainingFraction": -0.5 }""", 1.0)]
    // Above 1 clamps to fully available.
    [InlineData("""{ "bucketId": "b", "window": "5h", "remainingFraction": 1.5 }""", 0.0)]
    public void ResolvesAndClampsRemainingFraction(string bucket, double expectedUsed)
    {
        var window = Assert.Single(AntigravityQuotaParser.Parse(Wrap(bucket)));
        Assert.Equal(expectedUsed, window.UsedRatio, 6);
    }

    [Fact]
    public void ParsesEpochSecondsResetTimeAsFallback()
    {
        var window = Assert.Single(AntigravityQuotaParser.Parse(
            Wrap("""{ "bucketId": "b", "window": "weekly", "remainingFraction": 0.5, "resetTime": 1790000000 }""")));
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1790000000), window.ResetTime);
    }

    [Theory]
    // No usable fraction → skipped, never assumed fully spent.
    [InlineData("""{ "bucketId": "b", "window": "5h", "resetTime": "2026-06-21T05:03:00Z" }""")]
    // Disabled bucket → omitted.
    [InlineData("""{ "bucketId": "b", "window": "5h", "remainingFraction": 0.5, "disabled": true }""")]
    // Missing / blank bucketId → cannot be reconciled.
    [InlineData("""{ "window": "5h", "remainingFraction": 0.5 }""")]
    [InlineData("""{ "bucketId": "   ", "window": "5h", "remainingFraction": 0.5 }""")]
    // Unrecognized or absent window → cannot be classified.
    [InlineData("""{ "bucketId": "b", "window": "monthly", "remainingFraction": 0.5 }""")]
    [InlineData("""{ "bucketId": "b", "remainingFraction": 0.5 }""")]
    public void SkipsUnusableBuckets(string bucket)
    {
        Assert.Empty(AntigravityQuotaParser.Parse(Wrap(bucket)));
    }

    [Fact]
    public void IgnoresUnknownWrapperFieldsAndUnparseableGroups()
    {
        const string json = """
        {
          "futureTopLevel": 7,
          "response": {
            "extra": "ignored",
            "groups": [
              { "displayName": "Future family", "buckets": "not-an-array" },
              { "buckets": [
                  { "bucketId": "gemini-5h", "window": "5h", "remainingFraction": 0.5, "unknown": true } ] }
            ]
          }
        }
        """;
        var window = Assert.Single(AntigravityQuotaParser.Parse(json));
        Assert.Equal("gemini-5h", window.Id);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{ "response": {} }""")]
    [InlineData("""{ "response": { "groups": [] } }""")]
    public void ReturnsEmptyWhenNoBuckets(string json)
    {
        Assert.Empty(AntigravityQuotaParser.Parse(json));
    }

    [Fact]
    public void MalformedJsonThrows()
    {
        Assert.ThrowsAny<System.Text.Json.JsonException>(() => AntigravityQuotaParser.Parse("{ not json"));
    }

    private static string Wrap(string bucketJson)
        => $$"""{ "response": { "groups": [ { "buckets": [ {{bucketJson}} ] } ] } }""";
}
