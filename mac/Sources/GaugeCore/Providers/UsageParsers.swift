import Foundation

public struct UsagePayload: Sendable, Equatable {
    public var plan: String?
    public var windows: [UsageWindow]
    public var resetCredits: Int?

    public init(plan: String?, windows: [UsageWindow], resetCredits: Int? = nil) {
        self.plan = plan
        self.windows = windows
        self.resetCredits = resetCredits
    }
}

/// `GET https://api.anthropic.com/api/oauth/usage` →
/// `five_hour` / `seven_day`: `{ utilization: 0–100, resets_at: ISO-8601 }`, plus the newer
/// additive `limits[]`. Shapes come from live responses, not memory.
public enum ClaudeUsageParser {
    public static func windows(from data: Data) throws -> [UsageWindow] {
        let root = try JSONObject.parse(data)
        var windows: [UsageWindow] = []
        if let window = window(root, "five_hour", .fiveHour) { windows.append(window) }
        if let window = window(root, "seven_day", .weekly) { windows.append(window) }
        windows += scopedWeeklyLimits(root)
        return windows
    }

    /// A null/absent object, or a null utilization, means the window has no data.
    private static func window(_ root: JSONObject, _ key: String, _ type: UsageWindowType) -> UsageWindow? {
        guard let object = root.object(key), let utilization = object.double("utilization") else { return nil }
        return UsageWindow(
            type: type,
            usedRatio: min(max(utilization / 100, 0), 1),
            resetTime: object.date("resets_at"),
            duration: type == .fiveHour ? 5 * 3600 : 7 * 86_400)
    }

    /// Only model-scoped weekly entries (`kind=weekly_scoped`, `group=weekly`) are added; the
    /// account-wide session/weekly entries duplicate the legacy fields. `is_active` is
    /// deliberately not a filter: a live, enforceable Fable limit has reported false.
    private static func scopedWeeklyLimits(_ root: JSONObject) -> [UsageWindow] {
        guard let limits = root.objects("limits") else { return [] }
        var windows: [UsageWindow] = []
        var seen = Set<String>()
        for limit in limits {
            guard limit.string("kind")?.lowercased() == "weekly_scoped",
                  limit.string("group")?.lowercased() == "weekly",
                  let percent = limit.double("percent"),
                  let model = limit.object("scope")?.object("model"),
                  let name = ProviderText.normalize(model.string("display_name")) else { continue }
            let slug = ProviderText.slug(ProviderText.normalize(model.string("id")) ?? name)
            guard !slug.isEmpty, slug != "all-models", seen.insert(slug).inserted else { continue }
            windows.append(UsageWindow(
                type: .weekly,
                providerID: "claude-weekly-scoped-\(slug)",
                groupLabel: name,
                usedRatio: min(max(percent / 100, 0), 1),
                resetTime: limit.date("resets_at"),
                duration: 7 * 86_400))
        }
        return windows
    }
}

/// `GET https://chatgpt.com/backend-api/wham/usage` → `plan_type`,
/// `rate_limit.primary_window` / `secondary_window`
/// (`{ used_percent, reset_at: epoch seconds, limit_window_seconds }`), optional
/// `additional_rate_limits[]`, and `rate_limit_reset_credits`.
public enum CodexUsageParser {
    static let knownPlans: [String: String] = [
        "plus": "Plus", "pro": "Pro", "free": "Free", "go": "Go",
        "business": "Business", "team": "Team", "enterprise": "Enterprise",
    ]

    public static func payload(from data: Data) throws -> UsagePayload {
        let root = try JSONObject.parse(data)
        var windows: [UsageWindow] = []
        if let rateLimit = root.object("rate_limit") {
            if let window = window(rateLimit, "primary_window", fallback: .fiveHour) { windows.append(window) }
            if let window = window(rateLimit, "secondary_window", fallback: .weekly) { windows.append(window) }
        }
        windows += additionalRateLimits(root)
        return UsagePayload(
            plan: ProviderText.planLabel(root.string("plan_type"), known: knownPlans),
            windows: windows,
            resetCredits: resetCredits(root))
    }

    /// `available_count` is the balance held; the sibling `applicable_available_count` is
    /// the subset spendable against the limit in force and reads 0 whenever nothing is
    /// capped, so it is not what a status display wants. Absent stays nil, not zero.
    private static func resetCredits(_ root: JSONObject) -> Int? {
        guard let count = root.object("rate_limit_reset_credits")?.int64("available_count"), count >= 0 else { return nil }
        return Int(min(count, Int64(Int.max)))
    }

    /// A window is classified by `limit_window_seconds`, never by its primary/secondary
    /// position: the weekly-only shape arrives as a 604,800-second `primary_window` with a
    /// null secondary. Once a duration is supplied it is the contract, so an unknown one is
    /// dropped rather than reinterpreted by position.
    private static func window(
        _ rateLimit: JSONObject, _ key: String, fallback: UsageWindowType,
        idPrefix: String? = nil, groupLabel: String? = nil
    ) -> UsageWindow? {
        guard let object = rateLimit.object(key), let usedPercent = object.double("used_percent") else { return nil }
        let resetTime = object.int64("reset_at").map { Date(timeIntervalSince1970: TimeInterval($0)) }
        let seconds = object.int64("limit_window_seconds")
        let type: UsageWindowType
        let duration: TimeInterval?
        if object.contains("limit_window_seconds") {
            guard let seconds, let classified = classify(seconds) else { return nil }
            type = classified
            duration = TimeInterval(seconds)
        } else {
            // Older responses omitted the duration and used positional semantics; keep that
            // without inventing a duration for the pace caption.
            type = fallback
            duration = nil
        }
        let id = idPrefix.map { "\($0)-\(seconds.map { String($0) } ?? type.rawValue.lowercased())" }
        return UsageWindow(
            type: type,
            providerID: id,
            groupLabel: groupLabel,
            usedRatio: min(max(usedPercent / 100, 0), 1),
            resetTime: resetTime,
            duration: duration)
    }

    private static func classify(_ seconds: Int64) -> UsageWindowType? {
        switch seconds {
        case 5 * 3600: .fiveHour
        case 7 * 86_400: .weekly
        default: nil
        }
    }

    /// Optional named model/feature limits, parsed lossily so a malformed entry never
    /// affects the account-wide windows.
    private static func additionalRateLimits(_ root: JSONObject) -> [UsageWindow] {
        guard let entries = root.objects("additional_rate_limits") else { return [] }
        var windows: [UsageWindow] = []
        var seen = Set<String>()
        for entry in entries {
            let name = ProviderText.normalize(entry.string("limit_name"))
            let feature = ProviderText.normalize(entry.string("metered_feature"))
            guard let displayName = displayName(name ?? feature),
                  let identity = feature ?? name,
                  let rateLimit = entry.object("rate_limit") else { continue }
            let slug = ProviderText.slug(identity)
            guard !slug.isEmpty else { continue }
            let keys: [(String, UsageWindowType)] = [("primary_window", .fiveHour), ("secondary_window", .weekly)]
            for (key, fallback) in keys {
                if let window = window(rateLimit, key, fallback: fallback,
                                       idPrefix: "codex-additional-\(slug)", groupLabel: displayName),
                   seen.insert(window.id).inserted {
                    windows.append(window)
                }
            }
        }
        return windows
    }

    private static func displayName(_ raw: String?) -> String? {
        guard let raw else { return nil }
        return ProviderText.slug(raw) == "gpt-reserve" ? "Luna Reserve" : raw
    }
}
