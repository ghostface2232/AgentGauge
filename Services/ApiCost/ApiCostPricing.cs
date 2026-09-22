using System.Text.RegularExpressions;
using Gauge.Models;

namespace Gauge.Services.ApiCost;

/// <summary>
/// Public API list prices, USD per million tokens, for the models the Claude Code and
/// Codex logs name. Bundled and updated per release rather than fetched live: the table is
/// small, a network dependency would add a privacy line for a number that is only an
/// estimate anyway, and a stale row is visibly stale (the model shows as unpriced) rather
/// than silently wrong. Reference: models.dev, checked 2026-09-18.
/// </summary>
public static class ApiCostPricing
{
    /// <summary>
    /// Rates for one model. <see cref="CacheWrite"/> null means the provider bills a cache
    /// write at the plain input rate (OpenAI). <see cref="LongContext"/> holds the rates
    /// applied when the prompt exceeds <see cref="LongContextThreshold"/> tokens.
    /// </summary>
    public sealed record Rates(
        decimal Input, decimal CacheRead, decimal? CacheWrite, decimal Output,
        long? LongContextThreshold = null, Rates? LongContext = null);

    private static readonly Regex DateSuffix = new(@"-\d{8}$", RegexOptions.Compiled);

    private static readonly Dictionary<string, Rates> Table = new(StringComparer.OrdinalIgnoreCase)
    {
        // ── Anthropic ─────────────────────────────────────────────────────
        ["claude-fable-5-1"] = new(10, 0.25m, 12.5m, 50),
        ["claude-fable-5"] = new(10, 1, 12.5m, 50),
        ["claude-opus-5"] = new(5, 0.5m, 6.25m, 25),
        ["claude-opus-4-8"] = new(5, 0.5m, 6.25m, 25),
        ["claude-opus-4-7"] = new(5, 0.5m, 6.25m, 25),
        ["claude-opus-4-6"] = new(5, 0.5m, 6.25m, 25),
        ["claude-opus-4-5"] = new(5, 0.5m, 6.25m, 25),
        ["claude-sonnet-5"] = new(2, 0.2m, 2.5m, 10),
        ["claude-sonnet-4-6"] = new(3, 0.3m, 3.75m, 15),
        ["claude-sonnet-4-5"] = new(3, 0.3m, 3.75m, 15),
        ["claude-haiku-4-5"] = new(1, 0.1m, 1.25m, 5),
        // ── OpenAI (Codex) — the 272K prompt tier doubles the rate ───────
        // "codex-auto-review" is not here: see Excluded.
        ["gpt-6-astra"] = new(10, 1, 12.5m, 50, 272_000, new(20, 2, 25, 75)),
        ["gpt-5.6"] = new(4, 0.4m, 5, 20, 272_000, new(8, 0.8m, 10, 30)),
        ["gpt-5.6-sol"] = new(4, 0.4m, 5, 20, 272_000, new(8, 0.8m, 10, 30)),
        ["gpt-5.6-terra"] = new(2, 0.2m, 2.5m, 12, 272_000, new(4, 0.4m, 5, 18)),
        ["gpt-5.6-luna"] = new(0.2m, 0.02m, 0.25m, 1.2m, 272_000, new(0.4m, 0.04m, 0.5m, 1.8m)),
        ["gpt-5.5"] = new(5, 0.5m, null, 30, 272_000, new(10, 1, null, 45)),
        ["gpt-5.4"] = new(2.5m, 0.25m, null, 15, 272_000, new(5, 0.5m, null, 22.5m)),
        ["gpt-5.4-mini"] = new(0.75m, 0.075m, null, 4.5m),
        ["gpt-5.4-nano"] = new(0.2m, 0.02m, null, 1.25m),
        ["gpt-5.3-codex"] = new(1.75m, 0.175m, null, 14),
        ["gpt-5.3-codex-spark"] = new(1.75m, 0.175m, null, 14),
        ["gpt-5.2"] = new(1.75m, 0.175m, null, 14),
        ["gpt-5.2-codex"] = new(1.75m, 0.175m, null, 14),
        ["gpt-5.1"] = new(1.25m, 0.125m, null, 10),
        ["gpt-5.1-codex"] = new(1.25m, 0.125m, null, 10),
        ["gpt-5"] = new(1.25m, 0.125m, null, 10),
        ["gpt-5-codex"] = new(1.25m, 0.125m, null, 10),
        ["gpt-5-mini"] = new(0.25m, 0.025m, null, 2),
    };

    /// <summary>
    /// The model name as the table keys it: trimmed, lower-cased, and with a dated snapshot
    /// suffix ("claude-opus-4-5-20251101") removed. Anything else is left alone — an alias
    /// such as "opus" or an internal name such as "codex-auto-review" stays unpriced on
    /// purpose rather than being guessed at.
    /// </summary>
    public static string Normalize(string model) => DateSuffix.Replace(model.Trim().ToLowerInvariant(), "");

    public static Rates? For(string model) => Table.TryGetValue(Normalize(model), out var rates) ? rates : null;

    /// <summary>
    /// Models left out of the estimate altogether — neither priced nor counted as unpriced,
    /// so they never turn the total into a "+" floor. "codex-auto-review" is Codex's own
    /// automatic reviewer: the user did not ask for that work, OpenAI publishes no API rate
    /// for it, and counting it would make every Codex total an open-ended floor. The ledger
    /// still records its rows, so including it later needs no rescan.
    /// </summary>
    private static readonly HashSet<string> Excluded = new(StringComparer.OrdinalIgnoreCase) { "codex-auto-review" };

    public static bool IsExcluded(string model) => Excluded.Contains(Normalize(model));

    /// <summary>
    /// Whether one response's prompt crosses its model's long-context threshold. The tier is
    /// a property of a single request, so this must be decided per response before any
    /// aggregation — a day's summed prompt tokens would cross it almost every day.
    /// </summary>
    public static bool IsLongContext(string model, TokenTotals response)
        => For(model) is { LongContextThreshold: { } threshold, LongContext: not null } && response.PromptTotal > threshold;

    /// <summary>
    /// The list-price cost of one response's <paramref name="tokens"/> under
    /// <paramref name="model"/>, or null when the model has no known rate. The whole response
    /// is priced at the long-context tier when its own prompt crosses the threshold, matching
    /// how the providers bill.
    /// </summary>
    public static decimal? Cost(string model, TokenTotals tokens) => Cost(model, tokens, IsLongContext(model, tokens));

    /// <summary>
    /// The list-price cost of <paramref name="tokens"/> — possibly many responses summed — at
    /// the tier those responses were individually found to be in.
    /// </summary>
    public static decimal? Cost(string model, TokenTotals tokens, bool longContext)
    {
        if (For(model) is not { } rates) return null;
        if (longContext && rates.LongContext is { } tier)
        {
            rates = tier;
        }
        var perMillion =
            tokens.Input * rates.Input
            + tokens.CacheRead * rates.CacheRead
            + tokens.CacheWrite * (rates.CacheWrite ?? rates.Input)
            // Anthropic bills the one-hour cache slice at twice the input rate.
            + tokens.CacheWrite1h * rates.Input * 2
            + tokens.Output * rates.Output;
        return perMillion / 1_000_000m;
    }
}
