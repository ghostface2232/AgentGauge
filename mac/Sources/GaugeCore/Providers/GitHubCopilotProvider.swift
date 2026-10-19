import Foundation

/// `GET https://api.github.com/copilot_internal/user` — the undocumented endpoint the editor
/// integrations use to show Copilot's quotas, so parsing is tolerant throughout.
public struct GitHubCopilotEndpoint: UsageEndpoint {
    static let url = URL(string: "https://api.github.com/copilot_internal/user")!

    public init() {}

    public func fetch(_ credential: ToolCredential, transport: any HTTPTransport, now: Date) async throws -> UsagePayload {
        var request = URLRequest(url: Self.url)
        request.setValue("token \(credential.accessToken)", forHTTPHeaderField: "Authorization")
        request.setValue("application/json", forHTTPHeaderField: "Accept")
        // The endpoint expects an editor identity; like Claude's claude-code User-Agent, this
        // matches the official Copilot client for interop, not deception.
        request.setValue("vscode/1.100.0", forHTTPHeaderField: "Editor-Version")
        request.setValue("copilot-chat/0.26.0", forHTTPHeaderField: "Editor-Plugin-Version")
        request.setValue("GitHubCopilotChat/0.26.0", forHTTPHeaderField: "User-Agent")
        let (data, response) = try await transport.send(request)
        try HTTPStatus.ensureSuccess(response, now: now)
        return try GitHubCopilotUsageParser.payload(from: data)
    }

    public func credentialPlan(_ credential: ToolCredential?) -> String? { nil }
}

/// Copilot meters monthly quotas (chat, code completions, premium requests) rather than
/// rolling windows, so each metered entry of `quota_snapshots` becomes one `billingCycle`
/// window keyed by its quota id, resetting on the account-level `quota_reset_date_utc` (the
/// per-quota `quota_reset_at` is unused — 0 in practice).
public enum GitHubCopilotUsageParser {
    static let knownPlans: [String: String] = [
        "individual": "Pro", "business": "Business", "enterprise": "Enterprise", "free": "Free",
    ]
    /// Display order for the known quotas; an unknown future one follows, by id.
    static let quotaOrder = ["chat", "completions", "premium_interactions"]

    public static func payload(from data: Data) throws -> UsagePayload {
        let root = try JSONObject.parse(data)
        return UsagePayload(
            plan: plan(copilotPlan: root.string("copilot_plan"), sku: root.string("access_type_sku")),
            windows: windows(root))
    }

    private static func windows(_ root: JSONObject) -> [UsageWindow] {
        guard let snapshots = root.object("quota_snapshots") else { return [] }
        // The field name says UTC; Timestamp reads a bare date as UTC, never local time.
        let reset = root.date("quota_reset_date_utc")
        // JSON object order is not preserved by the decoder, so order explicitly.
        let ids = snapshots.storage.keys.sorted { lhs, rhs in
            let left = quotaOrder.firstIndex(of: lhs.lowercased()) ?? quotaOrder.count
            let right = quotaOrder.firstIndex(of: rhs.lowercased()) ?? quotaOrder.count
            return left != right ? left < right : lhs < rhs
        }
        return ids.compactMap { id in
            // An unlimited quota or one without an entitlement is not a meaningful gauge, and
            // one without a usable fraction is skipped — never assumed spent.
            guard let quota = snapshots.object(id),
                  quota.bool("has_quota") != false, quota.bool("unlimited") != true,
                  let used = usedRatio(quota) else { return nil }
            // Absolute request counts ("128 / 300") when both halves are present; remaining
            // alone cannot produce a denominator.
            var usedCount: Int64?
            var limitCount: Int64?
            if let entitlement = quota.double("entitlement"), entitlement > 0, entitlement < 9e15,
               let remaining = quota.double("remaining") {
                limitCount = Int64(entitlement.rounded())
                usedCount = Int64(min(max(entitlement - remaining, 0), entitlement).rounded())
            }
            // Every quota shares the billingCycle type, so the type-derived label would read
            // "Usage" for all of them; the per-quota key keeps rows and alert titles distinct.
            return UsageWindow(type: .billingCycle, providerID: id, usedRatio: used, labelKey: labelKey(id),
                               resetTime: reset, usedCount: usedCount, limitCount: limitCount)
        }
    }

    /// From `percent_remaining`, else `remaining/entitlement`. Both of the latter are required:
    /// a missing `remaining` means unknown, not zero, and reading it as 100% used would fire
    /// a false danger alert.
    private static func usedRatio(_ quota: JSONObject) -> Double? {
        if let percentRemaining = quota.double("percent_remaining") {
            return min(max(1 - percentRemaining / 100, 0), 1)
        }
        if let entitlement = quota.double("entitlement"), entitlement > 0, let remaining = quota.double("remaining") {
            return min(max(1 - remaining / entitlement, 0), 1)
        }
        return nil
    }

    /// An unknown quota keeps its raw id as the key, which `L10n.get` passes through, so it
    /// shows its id rather than collapsing into the generic label.
    static func labelKey(_ quotaID: String) -> String {
        switch quotaID.lowercased() {
        case "chat": "Label_Copilot_Chat"
        case "completions": "Label_Copilot_Completions"
        case "premium_interactions": "Label_Copilot_Premium"
        default: quotaID
        }
    }

    /// The free tier shows in `access_type_sku` (e.g. "free_limited_copilot"); otherwise
    /// `copilot_plan`, where a paid individual plan is Copilot Pro.
    static func plan(copilotPlan: String?, sku: String?) -> String? {
        if sku?.lowercased().contains("free") == true { return "Free" }
        return ProviderText.planLabel(copilotPlan, known: knownPlans)
    }
}
