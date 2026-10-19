import Foundation

/// Token counts for one or more model responses, normalized so every provider means the
/// same thing by each field: `input` is the uncached prompt share only, with `cacheRead`
/// and `cacheWrite` the cached and newly cached shares beside it (Codex reports the cached
/// shares as subsets of its input count; Claude Code reports them separately — both land
/// here in the Claude shape). `cacheWrite1h` is Claude's one-hour cache slice, billed at
/// its own rate.
public struct TokenTotals: Sendable, Equatable, Codable {
    public var input: Int64
    public var cacheRead: Int64
    public var cacheWrite: Int64
    public var cacheWrite1h: Int64
    public var output: Int64

    public init(input: Int64 = 0, cacheRead: Int64 = 0, cacheWrite: Int64 = 0, cacheWrite1h: Int64 = 0, output: Int64 = 0) {
        self.input = input
        self.cacheRead = cacheRead
        self.cacheWrite = cacheWrite
        self.cacheWrite1h = cacheWrite1h
        self.output = output
    }

    public static let zero = TokenTotals()

    /// The largest count one parsed field may carry (a trillion tokens).
    static let maximumCount: Int64 = 1 << 40

    public var total: Int64 { Self.sum(input, cacheRead, cacheWrite, cacheWrite1h, output) }

    /// The whole prompt as the provider saw it — what a long-context price tier keys on.
    public var promptTotal: Int64 { Self.sum(input, cacheRead, cacheWrite, cacheWrite1h) }

    public var isZero: Bool { total == 0 }

    // Saturating, not trapping: these sums come from logs and a ledger file on disk, and a
    // corrupt value must clamp a total rather than crash the app at the same line on every
    // launch (the C# original wraps silently instead).
    public static func + (a: TokenTotals, b: TokenTotals) -> TokenTotals {
        TokenTotals(input: sum(a.input, b.input), cacheRead: sum(a.cacheRead, b.cacheRead),
                    cacheWrite: sum(a.cacheWrite, b.cacheWrite), cacheWrite1h: sum(a.cacheWrite1h, b.cacheWrite1h),
                    output: sum(a.output, b.output))
    }

    public static func - (a: TokenTotals, b: TokenTotals) -> TokenTotals {
        TokenTotals(input: difference(a.input, b.input), cacheRead: difference(a.cacheRead, b.cacheRead),
                    cacheWrite: difference(a.cacheWrite, b.cacheWrite),
                    cacheWrite1h: difference(a.cacheWrite1h, b.cacheWrite1h), output: difference(a.output, b.output))
    }

    private static func sum(_ values: Int64...) -> Int64 {
        values.reduce(0) { total, value in
            let (result, overflow) = total.addingReportingOverflow(value)
            return overflow ? (value > 0 ? .max : .min) : result
        }
    }

    private static func difference(_ a: Int64, _ b: Int64) -> Int64 {
        let (result, overflow) = a.subtractingReportingOverflow(b)
        return overflow ? (b < 0 ? .max : .min) : result
    }
}

/// One model response as read from a CLI's local session log: when it happened, which
/// model answered, what it consumed, and the identity that de-duplicates it against the
/// same response written again (Claude Code rewrites a streamed message several times;
/// Codex may re-emit a count).
public struct UsageLogRecord: Sendable, Equatable {
    public var timestamp: Date
    public var model: String
    public var tokens: TokenTotals
    public var key: String?
}

/// What one tool's local logs add up to for a local calendar month, priced at public API
/// list rates. It is an estimate of API-equivalent spend, never a bill: a subscriber pays a
/// flat fee, and models without a known rate contribute tokens but no dollars, so
/// `costUSD` is a floor whenever `unpricedTokens` is positive.
public struct ApiCostEstimate: Sendable, Equatable {
    public var tool: ToolKind
    public var year: Int
    public var month: Int
    public var costUSD: Decimal
    /// The token mix behind `costUSD` (unpriced and excluded models are not in it).
    public var pricedTokens: TokenTotals
    public var unpricedTokens: Int64
    public var unpricedModels: [String]
    public var scannedAt: Date

    public init(tool: ToolKind, year: Int, month: Int, costUSD: Decimal, pricedTokens: TokenTotals,
                unpricedTokens: Int64 = 0, unpricedModels: [String] = [], scannedAt: Date) {
        self.tool = tool
        self.year = year
        self.month = month
        self.costUSD = costUSD
        self.pricedTokens = pricedTokens
        self.unpricedTokens = unpricedTokens
        self.unpricedModels = unpricedModels
        self.scannedAt = scannedAt
    }

    /// Some model had no known rate, so `costUSD` is a lower bound ("≈ $X+").
    public var isFloor: Bool { unpricedTokens > 0 }

    /// Nothing recorded this month — shown as no chip, never "$0".
    public var isEmpty: Bool { pricedTokens.isZero && unpricedTokens == 0 }
}
