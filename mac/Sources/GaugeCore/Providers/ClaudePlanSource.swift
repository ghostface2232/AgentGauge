import Foundation

/// Enriches the Claude usage credential with the current server-side plan.
actor ClaudePlanSource: CredentialSource {
    private static let url = URL(string: "https://api.anthropic.com/api/oauth/profile")!
    private static let refreshInterval: TimeInterval = 24 * 60 * 60
    private static let retryInterval: TimeInterval = 15 * 60

    private let credentials: any CredentialSource
    private let transport: any HTTPTransport
    private let clock: any MonotonicClock
    private var identity: String?
    private var accountIdentity: String?
    private var plan: String?
    private var nextCheck: TimeInterval = 0

    init(_ credentials: any CredentialSource, transport: any HTTPTransport,
         clock: any MonotonicClock = SystemMonotonicClock()) {
        self.credentials = credentials
        self.transport = transport
        self.clock = clock
    }

    func read(interactive: Bool) async -> CredentialRead {
        let result = await credentials.read(interactive: interactive)
        if case .missing = result {
            identity = nil
            accountIdentity = nil
            plan = nil
        }
        guard case .available(var credential) = result, !credential.accessToken.isEmpty else { return result }

        // Tokens can rotate or switch accounts. Never reuse a plan under a different token.
        let currentIdentity = UsageIdentity.hash(subject: nil, token: credential.accessToken)
        let now = clock.now()
        if identity != currentIdentity || now >= nextCheck {
            if identity != currentIdentity {
                accountIdentity = nil
                plan = nil
            }
            identity = currentIdentity
            nextCheck = now + Self.retryInterval
            await refreshPlan(credential, identity: currentIdentity)
        }
        if identity == currentIdentity {
            credential.accountIdentity = accountIdentity ?? credential.accountIdentity
            credential.plan = plan ?? credential.plan
            credential.verifiedPlan = plan
        }
        return .available(credential)
    }

    private func refreshPlan(_ credential: ToolCredential, identity currentIdentity: String) async {
        var request = ClaudeEndpoint.oauthRequest(url: Self.url, credential: credential)
        request.timeoutInterval = 5
        guard let (data, response) = try? await transport.send(request),
              identity == currentIdentity, !Task.isCancelled else { return }

        guard (200..<300).contains(response.statusCode) else {
            if let retryAfter = HTTPStatus.retryAfter(response, now: Date()) {
                nextCheck = max(nextCheck, clock.now() + retryAfter)
            }
            return
        }
        guard let profile = try? JSONObject.parse(data), let organization = profile.object("organization") else { return }
        if let accountId = ProviderText.normalize(profile.object("account")?.string("uuid")),
           let organizationId = ProviderText.normalize(organization.string("uuid")) {
            let verifiedIdentity = UsageIdentity.hash(subject: "\(accountId)\0\(organizationId)", token: credential.accessToken)
            if let accountIdentity, accountIdentity != verifiedIdentity { plan = nil }
            accountIdentity = verifiedIdentity
        }
        var display = ProviderText.normalize(organization.string("plan_display_name"))
        if display == nil || display == "Max",
           let type = organization.string("organization_type")?.lowercased(), type.hasPrefix("claude_") {
            display = CredentialParsing.claudePlan(subscriptionType: String(type.dropFirst("claude_".count)),
                                                   rateLimitTier: organization.string("rate_limit_tier")) ?? display
        }
        guard let display else { return }
        plan = display
        nextCheck = clock.now() + Self.refreshInterval
    }
}
