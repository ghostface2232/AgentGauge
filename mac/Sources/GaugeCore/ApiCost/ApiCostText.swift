import Foundation

/// The card's API-cost caption beside the plan label ("≈ $12.34") and its tooltip. Dollars
/// are machine-facing here (a fixed "12.34" / "1,235" shape), so they are formatted
/// without a locale; the token counts take the UI language's digit grouping.
public enum ApiCostText {
    private static let wholeDollarThreshold = Decimal(100)

    /// The chip text, or nil when there is nothing to show (an empty month reads as no chip,
    /// never "$0"). From $100 the cents are dropped: an estimate gains nothing from them, and
    /// a longer amount crowds the header. The threshold is checked after rounding to cents, so
    /// 99.996 reads "100", not "100.00". "+" marks a floor (some model had no known rate).
    public static func chip(_ estimate: ApiCostEstimate) -> String? {
        guard !estimate.isEmpty else { return nil }
        let cents = rounded(estimate.costUSD, scale: 2)
        let dollars = cents >= wholeDollarThreshold
            ? grouped(wholeNumber(rounded(cents, scale: 0)))
            : twoDecimals(cents)
        return L10n.format(estimate.isFloor ? "ApiCost_ValueFloor" : "ApiCost_Value", dollars)
    }

    /// The spelled-out explanation: an estimate from local logs at list prices, unrelated to
    /// the subscription, with the token mix and — for a floor — the unpriced models.
    public static func tooltip(_ estimate: ApiCostEstimate) -> String {
        let tokens = estimate.pricedTokens
        var text = L10n.format("Tooltip_ApiCost", count(tokens.input), count(tokens.cacheRead),
                               count(tokens.cacheWrite + tokens.cacheWrite1h), count(tokens.output))
        if estimate.isFloor {
            text += L10n.format("ApiCost_Unpriced", estimate.unpricedModels.joined(separator: ", "), count(estimate.unpricedTokens))
        }
        return text
    }

    /// The accessibility label: the amount first, then the explanation — a label replaces the
    /// visible text for VoiceOver, so the explanation alone would never say the number.
    public static func accessibilityLabel(_ estimate: ApiCostEstimate) -> String? {
        chip(estimate).map { $0 + ". " + tooltip(estimate) }
    }

    // Half away from zero, like the Windows card (costs are never negative).
    private static func rounded(_ value: Decimal, scale: Int) -> Decimal {
        var input = value
        var result = Decimal()
        NSDecimalRound(&result, &input, scale, .plain)
        return result
    }

    private static func wholeNumber(_ value: Decimal) -> Int64 {
        NSDecimalNumber(decimal: value).int64Value
    }

    private static func twoDecimals(_ cents: Decimal) -> String {
        let hundredths = wholeNumber(rounded(cents * 100, scale: 0))
        return "\(hundredths / 100)." + String(format: "%02lld", hundredths % 100)
    }

    private static func grouped(_ value: Int64) -> String {
        var digits = Array(String(value))
        var index = digits.count - 3
        while index > 0 {
            digits.insert(",", at: index)
            index -= 3
        }
        return String(digits)
    }

    private static func count(_ value: Int64) -> String {
        let formatter = NumberFormatter()
        formatter.locale = L10n.locale
        formatter.numberStyle = .decimal
        return formatter.string(from: NSNumber(value: value)) ?? String(value)
    }
}
