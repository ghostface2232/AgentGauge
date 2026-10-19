import Foundation

/// `GET https://cursor.com/api/usage-summary`, authenticated by Cursor's web-session cookie
/// (`WorkosCursorSessionToken=<userId>::<jwt>`, the separator URL-encoded) rather than a
/// bearer token.
public struct CursorEndpoint: UsageEndpoint {
    static let url = URL(string: "https://cursor.com/api/usage-summary")!

    public init() {}

    public func fetch(_ credential: ToolCredential, transport: any HTTPTransport, now: Date) async throws -> UsagePayload {
        // The cookie needs both halves; a JWT without its user id cannot authenticate.
        guard let userID = credential.accountID, !userID.isEmpty else {
            throw UsageFetchError.credentialUnreadable(.unreadable)
        }
        var request = URLRequest(url: Self.url)
        request.setValue("application/json", forHTTPHeaderField: "Accept")
        request.setValue("WorkosCursorSessionToken=\(userID)%3A%3A\(credential.accessToken)", forHTTPHeaderField: "Cookie")
        // The shared transport is ephemeral, but never let URLSession add or keep cookies of
        // its own beside the one set here.
        request.httpShouldHandleCookies = false
        let (data, response) = try await transport.send(request)
        try HTTPStatus.ensureSuccess(response, now: now)
        return try CursorUsageParser.payload(from: data)
    }

    public func credentialPlan(_ credential: ToolCredential?) -> String? { nil }
}

/// Cursor bills by credit consumption over a billing cycle rather than rolling 5h/weekly
/// windows, so its usage is one `billingCycle` window with `billingCycleEnd` as the reset.
public enum CursorUsageParser {
    static let knownPlans: [String: String] = [
        "free": "Free", "hobby": "Hobby", "pro": "Pro", "pro_plus": "Pro+", "pro-plus": "Pro+",
        "ultra": "Ultra", "business": "Business", "team": "Team", "enterprise": "Enterprise",
    ]

    /// Schema drift (renamed blocks, a plan shape with none of the recognized fields) fails
    /// the fetch instead of fabricating a 0% "success", which would replace the last good
    /// snapshot and could read as a reset to the notification evaluator.
    public static func payload(from data: Data) throws -> UsagePayload {
        let root = try JSONObject.parse(data)
        guard let percent = planPercentUsed(root) else { throw UsageFetchError.invalidResponse }
        let window = UsageWindow(
            type: .billingCycle,
            usedRatio: min(max(percent / 100, 0), 1),
            resetTime: root.date("billingCycleEnd"))
        return UsagePayload(plan: ProviderText.planLabel(root.string("membershipType"), known: knownPlans), windows: [window])
    }

    /// Headline percent, mirroring Cursor's dashboard precedence: plan.totalPercentUsed →
    /// avg(auto, api) → either lane → plan used/limit → overall (personal cap) → pooled
    /// (team). All values are already percents. Nil when no recognized field is present.
    static func planPercentUsed(_ root: JSONObject) -> Double? {
        let individual = root.object("individualUsage")
        let plan = individual?.object("plan")

        if let total = plan?.double("totalPercentUsed") { return clamp(total) }
        let auto = plan?.double("autoPercentUsed")
        let api = plan?.double("apiPercentUsed")
        if let auto, let api { return clamp((auto + api) / 2) }
        if let api { return clamp(api) }
        if let auto { return clamp(auto) }

        return ratioPercent(plan)
            ?? ratioPercent(individual?.object("overall"))
            ?? ratioPercent(root.object("teamUsage")?.object("pooled"))
    }

    /// used/limit as a clamped percent, or nil when the block or a positive limit is absent.
    private static func ratioPercent(_ block: JSONObject?) -> Double? {
        guard let block, let limit = block.double("limit"), limit > 0 else { return nil }
        return clamp((block.double("used") ?? 0) / limit * 100)
    }

    private static func clamp(_ value: Double) -> Double { min(max(value, 0), 100) }
}
