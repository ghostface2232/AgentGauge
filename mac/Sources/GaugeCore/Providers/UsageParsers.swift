import Foundation

public struct UsagePayload: Sendable, Equatable {
    public var plan: String?
    public var windows: [UsageWindow]
    public var resetCredits: Int?
    public var resetCreditDeadlines: [ResetCreditDeadline]?
    public var aiCreditsUsed: Double?
    var historyIdentity: String?
    var legacyCopilotTokenIdentity: String?

    public init(plan: String?, windows: [UsageWindow], resetCredits: Int? = nil,
                resetCreditDeadlines: [ResetCreditDeadline]? = nil) {
        self.plan = plan
        self.windows = windows
        self.resetCredits = resetCredits
        self.resetCreditDeadlines = resetCreditDeadlines
    }
}

/// `GET https://api.anthropic.com/api/oauth/usage` →
/// `five_hour` / `seven_day`: `{ utilization: 0–100, resets_at: ISO-8601 }`, plus the newer
/// additive `limits[]`. Shapes come from live responses, not memory.
public enum ClaudeUsageParser {
    public static func windows(from data: Data) throws -> [UsageWindow] {
        try payload(from: data).windows
    }

    static func payload(from data: Data, plan: String? = nil, now: Date = Date()) throws -> UsagePayload {
        let root = try JSONObject.parse(data)
        guard ["five_hour", "seven_day", "limits"].contains(where: root.contains) else {
            throw UsageFetchError.invalidResponse
        }
        var windows: [UsageWindow] = []
        if let window = window(root, "five_hour", .fiveHour) { windows.append(window) }
        if let window = window(root, "seven_day", .weekly) { windows.append(window) }
        windows += scopedWeeklyLimits(root)
        guard let program = root.object("cedar_ember"), program.bool("eligible") == true,
              let grants = program.objects("grants") else { return UsagePayload(plan: plan, windows: windows) }

        var deadlines: [ResetCreditDeadline] = []
        var balances: [String: Int] = [:]
        for grant in grants {
            guard let count = grant.int64("resets_left"), count > 0, count <= Int64(Int32.max),
                  grant.bool("paused") == false,
                  let starts = grant.date("starts_at"), starts <= now,
                  let ends = grant.date("ends_at"), ends > now else { continue }
            let clears = Set(grant.storage["clears"] as? [String] ?? [])
            let scope = clears == ["seven_day_overage_included"] ? "Fable" : nil
            if let index = deadlines.firstIndex(where: { $0.expiresAt == ends && $0.scope == scope }) {
                let total = min((deadlines[index].count ?? 0) + Int(count), Int(Int32.max))
                deadlines[index] = ResetCreditDeadline(expiresAt: ends, scope: scope, count: total)
            } else {
                deadlines.append(ResetCreditDeadline(expiresAt: ends, scope: scope, count: Int(count)))
            }
            for scope in clears { balances[scope] = min(balances[scope, default: 0] + Int(count), Int(Int32.max)) }
        }

        for index in windows.indices {
            let scope = switch windows[index].groupLabel {
            case "Fable": "seven_day_overage_included"
            case nil: windows[index].type == .fiveHour ? "five_hour" : "seven_day"
            default: nil as String?
            }
            if let scope { windows[index].resetCredits = balances[scope, default: 0] }
        }

        return UsagePayload(plan: plan, windows: windows, resetCreditDeadlines: deadlines)
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
    /// The Pro tiers are shown by their marketed monthly price. The API names them
    /// prolite/pro/promax (the Codex CLI's PlanType enum, 0.166), which read as internal ids.
    static let knownPlans: [String: String] = [
        "plus": "Plus", "prolite": "Pro 100", "pro": "Pro 200", "promax": "Pro 500",
        "free": "Free", "go": "Go",
        "business": "Business", "team": "Team", "enterprise": "Enterprise",
    ]

    public static func payload(from data: Data) throws -> UsagePayload {
        let root = try JSONObject.parse(data)
        guard root.contains("rate_limit") || root.contains("additional_rate_limits") else {
            throw UsageFetchError.invalidResponse
        }
        if let limits = root.storage["rate_limit"], !(limits is NSNull), JSONObject(limits) == nil {
            throw UsageFetchError.invalidResponse
        }
        if let additional = root.storage["additional_rate_limits"], !(additional is NSNull), !(additional is [Any]) {
            throw UsageFetchError.invalidResponse
        }
        var windows: [UsageWindow] = []
        if let rateLimit = root.object("rate_limit") {
            guard let parsed = accountWindows(rateLimit) else { throw UsageFetchError.invalidResponse }
            windows = parsed
        }
        windows += additionalRateLimits(root)
        return UsagePayload(
            plan: ProviderText.planLabel(root.string("plan_type"), known: knownPlans),
            windows: windows,
            resetCredits: resetCredits(root))
    }

    static func appServerPayload(from data: Data, accountID: String? = nil) throws -> UsagePayload? {
        let reply = try JSONObject.parse(data)
        guard let result = reply.object("result") else { return nil }
        if let accountID, let reportedID = result.string("accountId"), reportedID != accountID { return nil }
        let byID = result.object("rateLimitsByLimitId")
        let legacy = result.object("rateLimits")
        let legacyID = ProviderText.normalize(legacy?.string("limitId"))
        let account = byID?.object("codex") ?? (legacyID == nil || legacyID == "codex" ? legacy : nil)

        var windows: [UsageWindow] = []
        var seen = Set<String>()
        if let account {
            guard let parsed = accountWindows(account, appServer: true) else { return nil }
            windows = parsed.filter { seen.insert($0.id).inserted }
        }
        func addScopedWindows(_ id: String, limits: JSONObject) {
            let slug = ProviderText.slug(id)
            guard !slug.isEmpty else { return }
            let prefix = "codex-additional-\(slug)"
            let label = displayName(ProviderText.normalize(limits.string("limitName")) ?? id)
            let keys: [(String, UsageWindowType)] = [("primary", .fiveHour), ("secondary", .weekly)]
            for (key, fallback) in keys {
                if let entry = window(limits, key, fallback: fallback, idPrefix: prefix,
                                      groupLabel: label, appServer: true), seen.insert(entry.id).inserted {
                    windows.append(entry)
                }
            }
        }
        if let byID {
            for id in byID.storage.keys.sorted() where id != "codex" {
                if let limits = byID.object(id) { addScopedWindows(id, limits: limits) }
            }
        }
        if let legacy, let legacyID, legacyID != "codex" { addScopedWindows(legacyID, limits: legacy) }
        guard !windows.isEmpty else { return nil }
        let credits = result.object("rateLimitResetCredits")
        let count = credits?.int64("availableCount").flatMap { $0 >= 0 ? Int(exactly: $0) : nil }
        var expiries = Set<Date>()
        let deadlines = credits?.objects("credits")?.compactMap { entry -> ResetCreditDeadline? in
            guard entry.string("status") == "available", let epoch = entry.int64("expiresAt") else { return nil }
            let expiry = Date(timeIntervalSince1970: TimeInterval(epoch))
            return expiries.insert(expiry).inserted ? ResetCreditDeadline(expiresAt: expiry) : nil
        }
        return UsagePayload(plan: ProviderText.planLabel(account?.string("planType") ?? legacy?.string("planType"), known: knownPlans),
                            windows: windows, resetCredits: count, resetCreditDeadlines: deadlines)
    }

    static func resetCreditDeadlines(from data: Data) throws -> [ResetCreditDeadline]? {
        guard let entries = try JSONObject.parse(data).storage["credits"] as? [Any] else { return nil }
        var seen = Set<Date>()
        var deadlines: [ResetCreditDeadline] = []
        for value in entries {
            guard let entry = JSONObject(value), let status = entry.string("status"), !status.isEmpty else { return nil }
            guard status == "available" else { continue }
            guard let expiry = entry.storage["expires_at"], !(expiry is NSNull) else { continue }
            guard let expires = entry.date("expires_at") else { return nil }
            if seen.insert(expires).inserted { deadlines.append(ResetCreditDeadline(expiresAt: expires)) }
        }
        return deadlines
    }

    /// `available_count` is the balance held; the sibling `applicable_available_count` is
    /// the subset spendable against the limit in force and reads 0 whenever nothing is
    /// capped, so it is not what a status display wants. Absent stays nil, not zero.
    private static func resetCredits(_ root: JSONObject) -> Int? {
        guard let count = root.object("rate_limit_reset_credits")?.int64("available_count"), count >= 0 else { return nil }
        return Int(min(count, Int64(Int.max)))
    }

    /// A malformed reported account window invalidates the read, even alongside usable windows.
    private static func accountWindows(_ rateLimit: JSONObject, appServer: Bool = false) -> [UsageWindow]? {
        let keys: [(String, UsageWindowType)] = [
            (appServer ? "primary" : "primary_window", .fiveHour),
            (appServer ? "secondary" : "secondary_window", .weekly),
        ]
        var windows: [UsageWindow] = []
        for (key, fallback) in keys {
            guard let reported = rateLimit.storage[key], !(reported is NSNull) else { continue }
            guard let parsed = window(rateLimit, key, fallback: fallback, appServer: appServer) else { return nil }
            windows.append(parsed)
        }
        return windows
    }

    /// A window is classified by `limit_window_seconds`, never by its primary/secondary
    /// position: the weekly-only shape arrives as a 604,800-second `primary_window` with a
    /// null secondary. Once a duration is supplied it is the contract, so an unknown one is
    /// dropped rather than reinterpreted by position.
    private static func window(
        _ rateLimit: JSONObject, _ key: String, fallback: UsageWindowType,
        idPrefix: String? = nil, groupLabel: String? = nil, appServer: Bool = false
    ) -> UsageWindow? {
        guard let object = rateLimit.object(key),
              let usedPercent = object.double(appServer ? "usedPercent" : "used_percent") else { return nil }
        let resetTime = object.int64(appServer ? "resetsAt" : "reset_at").map { Date(timeIntervalSince1970: TimeInterval($0)) }
        let durationKey = appServer ? "windowDurationMins" : "limit_window_seconds"
        let seconds = object.int64(durationKey).flatMap { value -> Int64? in
            guard appServer else { return value }
            return (0...Int64.max / 60).contains(value) ? value * 60 : nil
        }
        let type: UsageWindowType
        let duration: TimeInterval?
        if object.contains(durationKey) && (!appServer || !(object.storage[durationKey] is NSNull)) {
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
