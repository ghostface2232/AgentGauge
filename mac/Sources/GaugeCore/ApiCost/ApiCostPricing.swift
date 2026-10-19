import Foundation

/// Public API list prices, USD per million tokens, for the models the Claude Code and
/// Codex logs name. Bundled and updated per release rather than fetched live: the table is
/// small, a network dependency would add a privacy line for a number that is only an
/// estimate anyway, and a stale row is visibly stale (the model shows as unpriced) rather
/// than silently wrong. Reference: the providers' own pricing pages
/// (platform.claude.com/docs/en/about-claude/pricing, developers.openai.com/api/docs/pricing),
/// checked 2026-10-19. Kept identical to the Windows table (Services/ApiCost/ApiCostPricing.cs).
public enum ApiCostPricing {
    /// One price tier. `cacheWrite` nil means the provider bills a cache write at the plain
    /// input rate (OpenAI).
    public struct Tier: Sendable, Equatable {
        public let input: Decimal
        public let cacheRead: Decimal
        public let cacheWrite: Decimal?
        public let output: Decimal
    }

    /// Rates for one model; `longContext` applies to a response whose prompt exceeds
    /// `longContextThreshold` tokens.
    public struct Rates: Sendable, Equatable {
        public let base: Tier
        public let longContextThreshold: Int64?
        public let longContext: Tier?
    }

    // Rates are written as strings: a Decimal float literal goes through Double, and 0.1
    // would not be exactly 0.1.
    private static func tier(_ input: String, _ cacheRead: String, _ cacheWrite: String?, _ output: String) -> Tier {
        Tier(input: decimal(input), cacheRead: decimal(cacheRead), cacheWrite: cacheWrite.map(decimal), output: decimal(output))
    }

    private static func rates(_ input: String, _ cacheRead: String, _ cacheWrite: String?, _ output: String,
                              longAbove threshold: Int64? = nil, _ longContext: Tier? = nil) -> Rates {
        Rates(base: tier(input, cacheRead, cacheWrite, output), longContextThreshold: threshold, longContext: longContext)
    }

    private static func decimal(_ text: String) -> Decimal {
        Decimal(string: text, locale: Locale(identifier: "en_US_POSIX"))!
    }

    private static let table: [String: Rates] = [
        // Anthropic. Fable 5.1, Mythos 5.1, Opus 5.5 and Sonnet 5.5 read cache below the usual
        // 0.1x of input (0.025x and 0.05x); writes keep the standard 1.25x.
        "claude-fable-5-1": rates("10", "0.25", "12.5", "50"),
        "claude-mythos-5-1": rates("10", "0.25", "12.5", "50"),
        "claude-fable-5": rates("10", "1", "12.5", "50"),
        "claude-mythos-5": rates("10", "1", "12.5", "50"),
        "claude-opus-5-5": rates("4", "0.2", "5", "20"),
        "claude-sonnet-5-5": rates("2", "0.1", "2.5", "10"),
        // Haiku 5.5 is the one current Claude model priced by prompt length: a request whose
        // prompt (input + cache read + cache write) passes 100K pays 5x for everything.
        "claude-haiku-5-5": rates("0.1", "0.01", "0.125", "0.5", longAbove: 100_000, tier("0.5", "0.05", "0.625", "2.5")),
        "claude-opus-5": rates("5", "0.5", "6.25", "25"),
        "claude-opus-4-8": rates("5", "0.5", "6.25", "25"),
        "claude-opus-4-7": rates("5", "0.5", "6.25", "25"),
        "claude-opus-4-6": rates("5", "0.5", "6.25", "25"),
        "claude-opus-4-5": rates("5", "0.5", "6.25", "25"),
        "claude-opus-4-1": rates("15", "1.5", "18.75", "75"),
        "claude-opus-4": rates("15", "1.5", "18.75", "75"),
        "claude-sonnet-5": rates("2", "0.2", "2.5", "10"),
        "claude-sonnet-4-6": rates("3", "0.3", "3.75", "15"),
        // Sonnet 4.5's 1M-context beta billed the whole request at a premium once the
        // prompt (input + cache read + cache write) passed 200K; later models dropped it.
        "claude-sonnet-4-5": rates("3", "0.3", "3.75", "15", longAbove: 200_000, tier("6", "0.6", "7.5", "22.5")),
        "claude-sonnet-4": rates("3", "0.3", "3.75", "15"),
        "claude-haiku-4-5": rates("1", "0.1", "1.25", "5"),
        "claude-haiku-3-5": rates("0.8", "0.08", "1", "4"),
        // OpenAI (Codex): the 272K prompt tier doubles the rate. "codex-auto-review" is not
        // here: see `excluded`. gpt-6.1-sol keeps gpt-6-sol's rates but reads cache at 0.05x.
        "gpt-6.1-sol": rates("2", "0.1", "2.5", "10", longAbove: 272_000, tier("4", "0.2", "5", "15")),
        "gpt-6-astra": rates("10", "1", "12.5", "50", longAbove: 272_000, tier("20", "2", "25", "75")),
        "gpt-6-sol": rates("2", "0.2", "2.5", "10", longAbove: 272_000, tier("4", "0.4", "5", "15")),
        "gpt-6-luna": rates("0.1", "0.01", "0.125", "0.5", longAbove: 272_000, tier("0.2", "0.02", "0.25", "0.75")),
        "gpt-5.6": rates("4", "0.4", "5", "20", longAbove: 272_000, tier("8", "0.8", "10", "30")),
        "gpt-5.6-sol": rates("4", "0.4", "5", "20", longAbove: 272_000, tier("8", "0.8", "10", "30")),
        "gpt-5.6-terra": rates("2", "0.2", "2.5", "12", longAbove: 272_000, tier("4", "0.4", "5", "18")),
        "gpt-5.6-luna": rates("0.2", "0.02", "0.25", "1.2", longAbove: 272_000, tier("0.4", "0.04", "0.5", "1.8")),
        "gpt-5.6-cyber": rates("12.5", "1.25", "15.625", "75"),
        "gpt-5.5": rates("5", "0.5", nil, "30", longAbove: 272_000, tier("10", "1", nil, "45")),
        "gpt-5.5-pro": rates("30", "0", nil, "180", longAbove: 272_000, tier("60", "0", nil, "270")),
        "gpt-5.5-cyber": rates("12.5", "1.25", nil, "75"),
        "gpt-5.4": rates("2.5", "0.25", nil, "15", longAbove: 272_000, tier("5", "0.5", nil, "22.5")),
        "gpt-5.4-pro": rates("30", "0", nil, "180", longAbove: 272_000, tier("60", "0", nil, "270")),
        "gpt-5.4-mini": rates("0.75", "0.075", nil, "4.5"),
        "gpt-5.4-nano": rates("0.2", "0.02", nil, "1.25"),
        "gpt-5.3-codex": rates("1.75", "0.175", nil, "14"),
        "gpt-5.3-codex-spark": rates("1.75", "0.175", nil, "14"),
        "gpt-5.2": rates("1.75", "0.175", nil, "14"),
        "gpt-5.2-pro": rates("21", "0", nil, "168"),
        "gpt-5.2-codex": rates("1.75", "0.175", nil, "14"),
        "gpt-5.1": rates("1.25", "0.125", nil, "10"),
        "gpt-5.1-codex": rates("1.25", "0.125", nil, "10"),
        "gpt-5.1-codex-max": rates("1.25", "0.125", nil, "10"),
        "gpt-5.1-codex-mini": rates("0.25", "0.025", nil, "2"),
        "gpt-5": rates("1.25", "0.125", nil, "10"),
        "gpt-5-pro": rates("15", "0", nil, "120"),
        "gpt-5-codex": rates("1.25", "0.125", nil, "10"),
        "gpt-5-mini": rates("0.25", "0.025", nil, "2"),
        "gpt-5-nano": rates("0.05", "0.005", nil, "0.4"),
    ]

    /// Models left out of the estimate altogether — neither priced nor counted as unpriced,
    /// so they never turn the total into a "+" floor. "codex-auto-review" is Codex's own
    /// automatic reviewer: the user did not ask for that work, OpenAI publishes no API rate
    /// for it, and counting it would make every Codex total an open-ended floor. The ledger
    /// still records its rows, so including it later needs no rescan.
    private static let excluded: Set<String> = ["codex-auto-review"]

    /// The model name as the table keys it: trimmed, lower-cased, and with a dated snapshot
    /// suffix ("claude-opus-4-5-20251101") removed. Anything else is left alone — an alias
    /// such as "opus" or an internal name such as "codex-auto-review" stays unpriced on
    /// purpose rather than being guessed at.
    public static func normalize(_ model: String) -> String {
        let name = model.trimmingCharacters(in: .whitespacesAndNewlines).lowercased()
        let suffix = name.suffix(9)
        if suffix.count == 9, suffix.first == "-", suffix.dropFirst().allSatisfy({ $0.isASCII && $0.isNumber }) {
            return String(name.dropLast(9))
        }
        return name
    }

    public static func rates(for model: String) -> Rates? { table[normalize(model)] }

    public static func isExcluded(_ model: String) -> Bool { excluded.contains(normalize(model)) }

    /// Whether one response's prompt crosses its model's long-context threshold. The tier is
    /// a property of a single request, so this must be decided per response before any
    /// aggregation — a day's summed prompt tokens would cross it almost every day.
    public static func isLongContext(_ model: String, _ response: TokenTotals) -> Bool {
        guard let rates = rates(for: model), let threshold = rates.longContextThreshold, rates.longContext != nil else {
            return false
        }
        return response.promptTotal > threshold
    }

    /// The list-price cost of one response's tokens, or nil when the model has no known
    /// rate. The whole response is priced at the long-context tier when its own prompt
    /// crosses the threshold, matching how the providers bill.
    public static func cost(_ model: String, _ tokens: TokenTotals) -> Decimal? {
        cost(model, tokens, longContext: isLongContext(model, tokens))
    }

    /// The list-price cost of `tokens` — possibly many responses summed — at the tier those
    /// responses were individually found to be in.
    public static func cost(_ model: String, _ tokens: TokenTotals, longContext: Bool) -> Decimal? {
        guard let rates = rates(for: model) else { return nil }
        let tier = longContext ? rates.longContext ?? rates.base : rates.base
        var perMillion = Decimal(tokens.input) * tier.input
        perMillion += Decimal(tokens.cacheRead) * tier.cacheRead
        perMillion += Decimal(tokens.cacheWrite) * (tier.cacheWrite ?? tier.input)
        // Anthropic bills the one-hour cache slice at twice the input rate.
        perMillion += Decimal(tokens.cacheWrite1h) * tier.input * 2
        perMillion += Decimal(tokens.output) * tier.output
        return perMillion / 1_000_000
    }
}
