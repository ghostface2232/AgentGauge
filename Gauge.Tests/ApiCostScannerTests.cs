using Gauge.Models;
using Gauge.Services;
using Gauge.Services.ApiCost;
using Gauge.ViewModels;

namespace Gauge.Tests;

/// <summary>
/// Pricing, the incremental scan over session logs, and the card's cost caption: rates per
/// token class and tier, unknown models kept out of the dollars but visible as a floor, a
/// response written twice counted once, resumption from the last complete line, and a
/// shrunk file rescanned without double counting.
/// </summary>
public sealed class ApiCostScannerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "GaugeApiCost_" + Guid.NewGuid().ToString("N"));
    private readonly MutableTime _time = new(new DateTimeOffset(2026, 9, 18, 12, 0, 0, TimeSpan.Zero));
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;

    private string ClaudeRoot => Path.Combine(_dir, "claude");
    private string CodexRoot => Path.Combine(_dir, "codex");
    private string LedgerPath => Path.Combine(_dir, "ledger.json");

    // ── Pricing ─────────────────────────────────────────────────────────────

    [Fact]
    public void PricesEveryTokenClassAtItsOwnRate()
    {
        // claude-opus-5: input 5, cache read 0.5, cache write 6.25, output 25 per million;
        // the one-hour cache slice at twice the input rate.
        var cost = ApiCostPricing.Cost("claude-opus-5", new TokenTotals(1_000_000, 1_000_000, 1_000_000, 1_000_000, 1_000_000));
        Assert.Equal(5m + 0.5m + 6.25m + 10m + 25m, cost);
    }

    [Fact]
    public void OpenAiCacheWritesFallBackToTheInputRate()
        // Kept under the 272K tier so only the cache-write fallback is under test.
        => Assert.Equal(0.25m, ApiCostPricing.Cost("gpt-5.4", new TokenTotals(0, 0, 100_000, 0, 0)));

    [Fact]
    public void ALongPromptIsPricedAtTheLongContextTier()
    {
        var shortPrompt = new TokenTotals(272_000, 0, 0, 0, 1_000);
        var longPrompt = new TokenTotals(272_001, 0, 0, 0, 1_000);
        Assert.Equal((272_000 * 5m + 1_000 * 30m) / 1_000_000m, ApiCostPricing.Cost("gpt-5.5", shortPrompt));
        Assert.Equal((272_001 * 10m + 1_000 * 45m) / 1_000_000m, ApiCostPricing.Cost("gpt-5.5", longPrompt));
    }

    [Fact]
    public void TheLongContextTierIsDecidedPerResponseNotPerDay()
    {
        // Two 200K-prompt responses on the same day sum to 400K, but neither crossed the
        // 272K threshold, so both bill at the base rate; a third, 300K response does cross.
        var lines = new[]
        {
            """{"timestamp":"2026-09-15T03:00:00Z","type":"turn_context","payload":{"model":"gpt-5.5"}}""",
            """{"timestamp":"2026-09-15T03:00:01Z","type":"token_usage_record","payload":{"response_id":"r1","usage":{"input_tokens":200000,"output_tokens":0}}}""",
            """{"timestamp":"2026-09-15T04:00:01Z","type":"token_usage_record","payload":{"response_id":"r2","usage":{"input_tokens":200000,"output_tokens":0}}}""",
            """{"timestamp":"2026-09-15T05:00:01Z","type":"token_usage_record","payload":{"response_id":"r3","usage":{"input_tokens":300000,"output_tokens":0}}}""",
        };
        WriteCodex("rollout.jsonl", lines);

        var estimate = Assert.Single(Scanner().Scan(Tools("Codex")));

        Assert.Equal((400_000 * 5m + 300_000 * 10m) / 1_000_000m, estimate.CostUsd);
    }

    [Fact]
    public void ABudgetLimitedScanReportsIncompleteAndTheNextResumes()
    {
        // A one-byte budget reads a single file per scan.
        WriteClaude("a.jsonl", Claude("m1", "2026-09-10T10:00:00Z", input: 1_000_000));
        WriteClaude("b.jsonl", Claude("m2", "2026-09-11T10:00:00Z", input: 1_000_000));
        var scanner = new ApiCostScanner(LedgerPath, Sources(), _time, Utc, maxBytesPerScan: 1);

        Assert.Equal(5m, scanner.Scan(Tools("Claude"))[0].CostUsd);
        Assert.False(scanner.LastScanComplete);

        Assert.Equal(10m, scanner.Scan(Tools("Claude"))[0].CostUsd);
        // The second file was read; nothing is left, which only a further scan can confirm.
        scanner.Scan(Tools("Claude"));
        Assert.True(scanner.LastScanComplete);
        Assert.Equal(10m, scanner.Scan(Tools("Claude"))[0].CostUsd);
    }

    [Theory]
    [InlineData("claude-opus-4-5-20251101", true)]
    [InlineData("CLAUDE-FABLE-5-1", true)]
    [InlineData(" gpt-5.5 ", true)]
    [InlineData("opus", false)]
    [InlineData("<synthetic>", false)]
    [InlineData("codex-auto-review", false)]
    [InlineData("unknown", false)]
    public void NormalizesSnapshotsButNeverGuessesAnAlias(string model, bool priced)
        => Assert.Equal(priced, ApiCostPricing.For(model) is not null);

    // ── Scanning ────────────────────────────────────────────────────────────

    [Fact]
    public void SumsTheMonthAndCountsAResponseWrittenTwiceOnce()
    {
        WriteClaude("a.jsonl",
            Claude("msg_1", "2026-09-10T10:00:00Z", input: 1_000_000),
            Claude("msg_1", "2026-09-10T10:00:01Z", input: 1_000_000),   // the same streamed message again
            Claude("msg_2", "2026-09-11T10:00:00Z", output: 1_000_000),
            Claude("msg_3", "2026-08-31T23:00:00Z", input: 1_000_000));  // last month
        // A resumed session copies an earlier message into a new file.
        WriteClaude("b.jsonl", Claude("msg_2", "2026-09-11T10:00:00Z", output: 1_000_000));

        var estimate = Assert.Single(Scanner().Scan(Tools("Claude")));

        Assert.Equal(5m + 25m, estimate.CostUsd);
        Assert.Equal(new TokenTotals(1_000_000, 0, 0, 0, 1_000_000), estimate.PricedTokens);
        Assert.False(estimate.HasUnpriced);
        Assert.Equal((2026, 9), (estimate.Year, estimate.Month));
    }

    [Fact]
    public void UnknownModelsCountTokensButNoDollars()
    {
        WriteClaude("a.jsonl",
            Claude("m1", "2026-09-10T10:00:00Z", input: 1_000_000),
            Claude("m2", "2026-09-10T11:00:00Z", input: 700, model: "<synthetic>"));

        var estimate = Assert.Single(Scanner().Scan(Tools("Claude")));

        Assert.Equal(5m, estimate.CostUsd);
        Assert.Equal(700, estimate.UnpricedTokens);
        Assert.Equal(["<synthetic>"], estimate.UnpricedModels);
        Assert.True(estimate.HasUnpriced);
    }

    [Fact]
    public void CodexAutoReviewIsLeftOutWithoutMakingTheTotalAFloor()
    {
        WriteCodex("rollout.jsonl",
            """{"timestamp":"2026-09-15T03:00:00Z","type":"turn_context","payload":{"model":"gpt-5.3-codex"}}""",
            """{"timestamp":"2026-09-15T03:00:01Z","type":"token_usage_record","payload":{"response_id":"r1","usage":{"input_tokens":1000000,"output_tokens":0}}}""",
            """{"timestamp":"2026-09-15T03:01:00Z","type":"turn_context","payload":{"model":"codex-auto-review"}}""",
            """{"timestamp":"2026-09-15T03:01:01Z","type":"token_usage_record","payload":{"response_id":"r2","usage":{"input_tokens":5000000,"output_tokens":9000}}}""");

        var estimate = Assert.Single(Scanner().Scan(Tools("Codex")));

        Assert.Equal(1.75m, estimate.CostUsd);
        Assert.False(estimate.HasUnpriced);
        Assert.Empty(estimate.UnpricedModels);
        // Still recorded, so including it later needs no rescan.
        Assert.Contains(ApiCostLedger.Load(LedgerPath).Files.Values.SelectMany(f => f.Days), r => r.Model == "codex-auto-review");
    }

    [Fact]
    public void AnAppendedFileIsReadFromWhereTheLastScanStopped()
    {
        var path = WriteClaude("a.jsonl", Claude("m1", "2026-09-10T10:00:00Z", input: 1_000_000));
        Assert.Equal(5m, Scanner().Scan(Tools("Claude"))[0].CostUsd);

        // A complete line plus one still being written (no newline yet).
        File.AppendAllText(path, Claude("m2", "2026-09-12T10:00:00Z", input: 1_000_000) + "\n"
            + Claude("m3", "2026-09-12T11:00:00Z", input: 1_000_000));
        Assert.Equal(10m, Scanner().Scan(Tools("Claude"))[0].CostUsd);

        // The partial line completes: it is read once, and nothing before it again.
        File.AppendAllText(path, "\n");
        Assert.Equal(15m, Scanner().Scan(Tools("Claude"))[0].CostUsd);
        Assert.Equal(15m, Scanner().Scan(Tools("Claude"))[0].CostUsd);
    }

    [Fact]
    public void AShrunkFileIsForgottenAndReadAgainWithoutDoubleCounting()
    {
        var path = WriteClaude("a.jsonl",
            Claude("m1", "2026-09-10T10:00:00Z", input: 1_000_000),
            Claude("m2", "2026-09-10T11:00:00Z", input: 1_000_000));
        Assert.Equal(10m, Scanner().Scan(Tools("Claude"))[0].CostUsd);

        File.WriteAllText(path, Claude("m1", "2026-09-10T10:00:00Z", input: 1_000_000) + "\n");
        Assert.Equal(5m, Scanner().Scan(Tools("Claude"))[0].CostUsd);
    }

    [Fact]
    public void CodexResumesItsPerFileStateAcrossScans()
    {
        // gpt-5.3-codex has no long-context tier, so a million-token total prices flat.
        var path = WriteCodex("rollout.jsonl",
            """{"timestamp":"2026-09-15T03:00:00Z","type":"turn_context","payload":{"model":"gpt-5.3-codex"}}""",
            """{"timestamp":"2026-09-15T03:00:01Z","type":"event_msg","payload":{"type":"token_count","info":{"total_token_usage":{"input_tokens":1000000,"cached_input_tokens":0,"output_tokens":0}}}}""");
        Assert.Equal(1.75m, Assert.Single(Scanner().Scan(Tools("Codex"))).CostUsd);

        // Only the running total is written: the next response is its increase, still
        // priced as gpt-5.3-codex from the turn context read in the previous scan.
        File.AppendAllText(path,
            """{"timestamp":"2026-09-15T04:00:00Z","type":"event_msg","payload":{"type":"token_count","info":{"total_token_usage":{"input_tokens":2000000,"cached_input_tokens":0,"output_tokens":0}}}}""" + "\n");
        Assert.Equal(3.5m, Assert.Single(Scanner().Scan(Tools("Codex"))).CostUsd);
    }

    [Fact]
    public void OnlyTheRequestedToolsAreRead()
    {
        WriteClaude("a.jsonl", Claude("m1", "2026-09-10T10:00:00Z", input: 1_000_000));
        WriteCodex("r.jsonl",
            """{"timestamp":"2026-09-15T03:00:00Z","type":"turn_context","payload":{"model":"gpt-5.4"}}""",
            """{"timestamp":"2026-09-15T03:00:01Z","type":"token_usage_record","payload":{"response_id":"r1","usage":{"input_tokens":1000000,"output_tokens":0}}}""");

        var estimates = Scanner().Scan(Tools("Codex"));

        Assert.Equal("Codex", Assert.Single(estimates).ToolName);
        Assert.DoesNotContain(ApiCostLedger.Load(LedgerPath).Files.Values, f => f.Tool == "Claude");
    }

    [Fact]
    public void DaysFollowTheGivenZone()
    {
        // 23:30 UTC on 31 August is already 1 September in Seoul.
        WriteClaude("a.jsonl", Claude("m1", "2026-08-31T23:30:00Z", input: 1_000_000));
        var seoul = TimeZoneInfo.CreateCustomTimeZone("KST", TimeSpan.FromHours(9), "KST", "KST");

        Assert.Equal(5m, new ApiCostScanner(LedgerPath, Sources(), _time, seoul).Scan(Tools("Claude"))[0].CostUsd);
        File.Delete(LedgerPath);
        Assert.Equal(0m, Scanner().Scan(Tools("Claude"))[0].CostUsd);
    }

    [Fact]
    public void AnUnreadableLedgerIsRebuiltFromTheLogs()
    {
        WriteClaude("a.jsonl", Claude("m1", "2026-09-10T10:00:00Z", input: 1_000_000));
        Directory.CreateDirectory(_dir);
        File.WriteAllText(LedgerPath, "{ not json");
        Assert.Equal(5m, Scanner().Scan(Tools("Claude"))[0].CostUsd);
    }

    [Fact]
    public void MissingLogRootsYieldAnEmptyEstimate()
    {
        var estimate = Assert.Single(Scanner().Scan(Tools("Claude")));
        Assert.True(estimate.IsEmpty);
    }

    // ── Card caption ────────────────────────────────────────────────────────

    [Fact]
    public void CardShowsTheEstimateBesidePlanAndMarksAFloor()
    {
        var card = new ToolCardViewModel(new CachedUsage { ToolName = "Claude" });
        var estimate = new ApiCostEstimate("Claude", 2026, 9, 1234.5m, new TokenTotals(1, 2, 3, 4, 5), 0, [], _time.Now);

        card.ApplyApiCost(estimate);
        Assert.True(card.HasApiCost);
        Assert.Equal("≈ $1234.50", card.ApiCostText);
        Assert.Contains("API", card.ApiCostDescription);
        Assert.Contains("입력 1", card.ApiCostDescription);

        card.ApplyApiCost(estimate with { UnpricedTokens = 42, UnpricedModels = ["codex-auto-review"] });
        Assert.Equal("≈ $1234.50+", card.ApiCostText);
        Assert.Contains("codex-auto-review", card.ApiCostDescription);

        card.ApplyApiCost(null);
        Assert.False(card.HasApiCost);
        card.ApplyApiCost(estimate with { CostUsd = 0, PricedTokens = TokenTotals.Zero });
        Assert.False(card.HasApiCost);
    }

    [Fact]
    public void UsageViewModelKeepsEstimatesForCardsCreatedLater()
    {
        var vm = new UsageViewModel();
        vm.SetApiCosts([new ApiCostEstimate("Claude", 2026, 9, 3m, new TokenTotals(1, 0, 0, 0, 0), 0, [], _time.Now)]);
        vm.Apply(new UsageState
        {
            Tools =
            [
                new CachedUsage { ToolName = "Claude", Snapshot = new UsageSnapshot { ToolName = "Claude", Windows = [] } },
                new CachedUsage { ToolName = "Cursor", Snapshot = new UsageSnapshot { ToolName = "Cursor", Windows = [] } },
            ],
        });

        Assert.Equal("≈ $3.00", vm.Cards.Single(c => c.ToolName == "Claude").ApiCostText);
        Assert.False(vm.Cards.Single(c => c.ToolName == "Cursor").HasApiCost);

        vm.SetApiCosts([]);
        Assert.All(vm.Cards, c => Assert.False(c.HasApiCost));
    }

    // ── Settings ────────────────────────────────────────────────────────────

    [Fact]
    public void TheOptionIsOffUntilTurnedOnAndKeepsOtherKeys()
    {
        var store = new ApiCostSettingsStore(() => _dir);
        Assert.False(store.Load());

        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "settings.json"), """{ "ViewMode": "gauge", "WeeklyPaceModel": "workdays" }""");
        Assert.True(store.TrySave(true));

        Assert.True(store.Load());
        Assert.Equal(UsageViewMode.Gauge, new ViewModeSettingsStore(() => _dir).Load());
        Assert.Equal(WeeklyPaceModel.WorkDays, new WeeklyPaceModelSettingsStore(() => _dir).Load());
    }

    [Fact]
    public void GlobalSettingsRaiseTheToggleOnlyForUserChanges()
    {
        var vm = new GlobalSettingsViewModel(NotificationPreferences.Default, false, UsageViewMode.Bar, showApiCost: true);
        var requests = new List<bool>();
        vm.ApiCostToggleRequested += (_, show) => requests.Add(show);

        Assert.True(vm.ShowApiCost);
        vm.SetShowApiCost(false);
        vm.ShowApiCost = true;

        Assert.Equal([true], requests);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private ApiCostScanner Scanner() => new(LedgerPath, Sources(), _time, Utc);

    private IReadOnlyList<ApiCostScanner.LogSource> Sources() =>
    [
        new("Claude", ApiCostScanner.LogKind.Claude, [ClaudeRoot]),
        new("Codex", ApiCostScanner.LogKind.Codex, [CodexRoot]),
    ];

    private static IReadOnlySet<string> Tools(params string[] tools) => tools.ToHashSet(StringComparer.Ordinal);

    private static string Claude(string id, string timestamp, long input = 0, long output = 0, string model = "claude-opus-5")
        => "{\"type\":\"assistant\",\"timestamp\":\"" + timestamp + "\",\"requestId\":\"req_" + id + "\",\"sessionId\":\"s\","
           + "\"message\":{\"id\":\"" + id + "\",\"model\":\"" + model + "\",\"stop_reason\":\"end_turn\","
           + "\"usage\":{\"input_tokens\":" + input + ",\"cache_read_input_tokens\":0,\"cache_creation_input_tokens\":0,\"output_tokens\":" + output + "}}}";

    private string WriteClaude(string name, params string[] lines) => Write(ClaudeRoot, name, lines);
    private string WriteCodex(string name, params string[] lines) => Write(CodexRoot, name, lines);

    private static string Write(string root, string name, string[] lines)
    {
        var folder = Path.Combine(root, "project");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, name);
        File.WriteAllText(path, string.Join("\n", lines) + "\n");
        return path;
    }

    private sealed class MutableTime(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
        catch { /* best effort */ }
    }
}
