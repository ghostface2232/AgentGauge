import CryptoKit
import Foundation

public struct ProviderOutcome: Sendable {
    public let snapshot: UsageSnapshot
    /// False when no credential exists (a clean signed-out state).
    public let signedIn: Bool
    /// True only for a fresh network read — not a cache serve inside a cap or cooldown.
    /// A cache serve is a "success" that does not prove the server accepted the token.
    public let isLive: Bool
}

/// Every data source implements this and normalizes into `UsageSnapshot`; the UI never
/// depends on a provider's implementation.
public protocol UsageProvider: Sendable {
    var tool: ToolKind { get }
    func fetch(_ interaction: FetchInteraction) async throws -> ProviderOutcome
}

/// The per-tool part of an HTTP usage provider: its request and parsing.
public protocol UsageEndpoint: Sendable {
    func fetch(_ credential: ToolCredential, transport: any HTTPTransport, now: Date) async throws -> UsagePayload
    /// The plan knowable from the credential alone (Claude's), shown even without a usage
    /// response and refreshed on every cache serve. Nil when it only comes from the response.
    func credentialPlan(_ credential: ToolCredential?) -> String?
}

public struct ClaudeEndpoint: UsageEndpoint {
    static let url = URL(string: "https://api.anthropic.com/api/oauth/usage?cedar_ember=1")!
    static let plainURL = URL(string: "https://api.anthropic.com/api/oauth/usage")!
    // The endpoint buckets the claude-code agent less aggressively than arbitrary ones —
    // the one place a known client's UA is matched, for interop, not deception.
    static let userAgent = "claude-cli/2.1.321 (external, cli)"

    public init() {}

    public func fetch(_ credential: ToolCredential, transport: any HTTPTransport, now: Date) async throws -> UsagePayload {
        var request = URLRequest(url: Self.url)
        request.setValue("Bearer \(credential.accessToken)", forHTTPHeaderField: "Authorization")
        request.setValue("oauth-2025-04-20", forHTTPHeaderField: "anthropic-beta")
        request.setValue(Self.userAgent, forHTTPHeaderField: "User-Agent")
        request.setValue("application/json", forHTTPHeaderField: "Accept")
        var (data, response) = try await transport.send(request)
        if response.statusCode == 400 {
            // An older backend may reject the optional query. Keep ordinary usage available.
            request.url = Self.plainURL
            request.setValue("claude-code/2.1.179", forHTTPHeaderField: "User-Agent")
            (data, response) = try await transport.send(request)
        }
        try HTTPStatus.ensureSuccess(response, now: now)
        return try ClaudeUsageParser.payload(from: data, plan: credential.plan, now: now)
    }

    public func credentialPlan(_ credential: ToolCredential?) -> String? { credential?.plan }
}

public actor CodexEndpoint: UsageEndpoint {
    static let url = URL(string: "https://chatgpt.com/backend-api/wham/usage")!
    static let resetCreditsURL = URL(string: "https://chatgpt.com/backend-api/wham/rate-limit-reset-credits")!
    private static let resetCreditRefreshInterval: TimeInterval = 10 * 60
    private var creditCache: (identity: String, balance: Int, checkedAt: TimeInterval, deadlines: [ResetCreditDeadline]?)?
    private var creditGate = RateLimitGate(backoff: BackoffPolicy(base: resetCreditRefreshInterval, maximum: 30 * 60))
    private let clock: any MonotonicClock

    public init(clock: any MonotonicClock = SystemMonotonicClock()) {
        self.clock = clock
    }

    public func fetch(_ credential: ToolCredential, transport: any HTTPTransport, now: Date) async throws -> UsagePayload {
        let (data, response) = try await transport.send(Self.request(Self.url, credential))
        try HTTPStatus.ensureSuccess(response, now: now)
        var payload = try CodexUsageParser.payload(from: data)
        payload.resetCreditDeadlines = try await readCreditDeadlines(payload.resetCredits, credential: credential,
                                                                     transport: transport, now: now)
        return payload
    }

    private static func request(_ url: URL, _ credential: ToolCredential) -> URLRequest {
        var request = URLRequest(url: url)
        request.setValue("Bearer \(credential.accessToken)", forHTTPHeaderField: "Authorization")
        request.setValue("Gauge/1.0", forHTTPHeaderField: "User-Agent")
        request.setValue("application/json", forHTTPHeaderField: "Accept")
        if let accountID = credential.accountID, !accountID.isEmpty {
            request.setValue(accountID, forHTTPHeaderField: "ChatGPT-Account-Id")
        }
        return request
    }

    private func readCreditDeadlines(_ balance: Int?, credential: ToolCredential, transport: any HTTPTransport, now: Date)
        async throws -> [ResetCreditDeadline]? {
        guard let balance, balance > 0 else {
            creditCache = nil
            return nil
        }

        let identity = credential.historyIdentity ?? UsageIdentity.hash(subject: nil, token: credential.accessToken)
        let checkedAt = clock.now()
        if let cached = creditCache, cached.identity == identity, cached.balance == balance,
           checkedAt - cached.checkedAt < Self.resetCreditRefreshInterval { return cached.deadlines }
        guard !creditGate.inCooldown(now: checkedAt) else { return nil }

        creditCache = (identity, balance, checkedAt, nil)
        var request = Self.request(Self.resetCreditsURL, credential)
        request.timeoutInterval = 4
        do {
            let (data, response) = try await transport.send(request)
            guard (200..<300).contains(response.statusCode) else {
                if RateLimitGate.isRetryable(response.statusCode) {
                    creditGate.recordFailure(retryAfter: HTTPStatus.retryAfter(response, now: now), now: clock.now())
                }
                Log.write("provider", "Codex reset deadlines unavailable: HTTP \(response.statusCode)")
                return nil
            }
            creditGate.recordSuccess()
            let deadlines = try CodexUsageParser.resetCreditDeadlines(from: data)
            creditCache = (identity, balance, checkedAt, deadlines)
            return deadlines
        } catch {
            try Task.checkCancellation()
            Log.write("provider", "Codex reset deadlines unavailable: \(type(of: error))")
            return nil
        }
    }

    public nonisolated func credentialPlan(_ credential: ToolCredential?) -> String? { nil }
}

/// The shared fetch skeleton (the Windows app's `UsageProviderBase`): the credential read,
/// the delegated refresh of an expired token, 401/403 → refresh → single retry, the empty
/// signed-out snapshot, and the throttle layer configured by `FetchPolicy`.
///
/// Two invariants: under a cache-serving policy a failure propagates ONLY when nothing is
/// cached (otherwise the coordinator would overwrite the cache with an empty success); and
/// a cooldown with nothing cached fails fast WITHOUT a network call, or a cold start would
/// hammer the throttling endpoint on every cycle and popover open.
///
/// The coordinator never runs two fetches of one tool at once, so actor reentrancy across
/// the awaits below cannot interleave two attempts.
public actor HTTPUsageProvider: UsageProvider {
    public nonisolated let tool: ToolKind
    private let credentials: any CredentialSource
    private let endpoint: any UsageEndpoint
    private let policy: FetchPolicy
    private let transport: any HTTPTransport
    private let refresher: (any DelegatedRefreshing)?
    private let clock: any MonotonicClock
    private let wallClock: @Sendable () -> Date

    private var gate: RateLimitGate?
    private var lastSnapshot: UsageSnapshot?
    private var lastSuccessAt: TimeInterval?
    private var fingerprint: String?
    private var fingerprintInitialized = false

    private var cachesSnapshots: Bool {
        gate != nil || policy.minimumFetchInterval > 0 || policy.serveCachedOnAnyFailure
    }

    public init(
        tool: ToolKind,
        credentials: any CredentialSource,
        endpoint: any UsageEndpoint,
        policy: FetchPolicy,
        transport: any HTTPTransport,
        refresher: (any DelegatedRefreshing)? = nil,
        clock: any MonotonicClock = SystemMonotonicClock(),
        wallClock: @escaping @Sendable () -> Date = { Date() }
    ) {
        self.tool = tool
        self.credentials = credentials
        self.endpoint = endpoint
        self.policy = policy
        self.transport = transport
        self.refresher = refresher
        self.clock = clock
        self.wallClock = wallClock
        gate = policy.retryBackoff.map { RateLimitGate(backoff: $0) }
    }

    public func fetch(_ interaction: FetchInteraction) async throws -> ProviderOutcome {
        let interactive = interaction == .user
        var read = await credentials.read(interactive: interactive)

        // An expired token is normal after a long idle: ask the CLI to refresh its own token
        // (it owns the rotation, so this cannot break its login), then re-read. Done before
        // the fingerprint check so a successful refresh is not read as an account switch.
        if case .invalid(let problem) = read, problem.isRefreshable, let refresher, await refresher.tryRefresh() {
            read = await credentials.read(interactive: interactive)
        }

        var credential: ToolCredential?
        if case .available(let available) = read { credential = available }
        // A read that saw nothing is not an account switch: treating it as one would drop the
        // happy-path cap and a standing cooldown, and the next good read would hit a throttling
        // endpoint at once. A real switch still shows when the credential can next be read.
        if cachesSnapshots && !read.leavesAccountUnknown { trackFingerprint(credential) }

        switch read {
        case .invalid(let problem) where problem.requiresSignIn:
            throw UsageFetchError.authenticationRequired
        case .invalid(let problem):
            throw UsageFetchError.credentialUnreadable(problem)
        case .available, .missing:
            break
        }

        // Inside the happy-path cap or a cooldown, serve the cache without a call. Only the
        // cooldown distinguishes who asked; the cap is a cost limit nobody pierces.
        let now = clock.now()
        let blocked = gate?.shouldBlock(interaction, now: now) ?? false
        let fetchedRecently = policy.minimumFetchInterval > 0
            && lastSuccessAt.map { now - $0 < policy.minimumFetchInterval } == true
        if let lastSnapshot, blocked || fetchedRecently {
            return cached(lastSnapshot, credential)
        }

        guard let credential, !credential.accessToken.isEmpty else {
            return ProviderOutcome(
                snapshot: UsageSnapshot(tool: tool, plan: endpoint.credentialPlan(credential), windows: [], capturedAt: wallClock()),
                signedIn: false, isLive: false)
        }
        if blocked { throw UsageFetchError.cooldown }

        do {
            return try await fetchWithPolicy(credential)
        } catch let error as UsageFetchError where error.isAuthRejection {
            // The server rejected a token that looked valid locally (revoked, or it expired
            // between read and call): one delegated refresh, then a single retry.
            if let refresher, await refresher.tryRefresh(),
               case .available(let fresh) = await credentials.read(interactive: false),
               !fresh.accessToken.isEmpty {
                // Adopt the rotated token's fingerprint first, or the next call would read
                // the rotation as an account switch and discard what this retry records.
                if cachesSnapshots { trackFingerprint(fresh) }
                do {
                    return try await fetchWithPolicy(fresh)
                } catch let retryError as UsageFetchError where retryError.isAuthRejection {
                    throw UsageFetchError.authenticationRequired
                }
            }
            throw UsageFetchError.authenticationRequired
        }
    }

    /// One attempt with the policy's degradation applied. Both the first attempt and the
    /// post-refresh retry go through here, so a 429 on the retry still arms the gate.
    private func fetchWithPolicy(_ credential: ToolCredential) async throws -> ProviderOutcome {
        do {
            let payload = try await endpoint.fetch(credential, transport: transport, now: wallClock())
            var snapshot = UsageSnapshot(
                tool: tool, plan: payload.plan, windows: payload.windows,
                resetCredits: payload.resetCredits, resetCreditDeadlines: payload.resetCreditDeadlines,
                capturedAt: wallClock())
            snapshot.historyIdentity = payload.historyIdentity ?? credential.historyIdentity
            snapshot.legacyCopilotTokenIdentity = payload.legacyCopilotTokenIdentity
            gate?.recordSuccess()
            if cachesSnapshots {
                lastSuccessAt = clock.now()
                lastSnapshot = snapshot
            }
            return ProviderOutcome(snapshot: snapshot, signedIn: true, isLive: true)
        } catch let error as UsageFetchError where error.isAuthRejection {
            throw error
        } catch UsageFetchError.http(let status, let retryAfter) where gate != nil && RateLimitGate.isRetryable(status) {
            let cooldown = gate?.recordFailure(retryAfter: retryAfter, now: clock.now()) ?? 0
            if let lastSnapshot {
                // Serving the cache reads as success downstream, so this is the only record.
                Log.write("provider", "\(tool.displayName) throttled (\(status) x\(gate?.consecutiveFailures ?? 0)), serving cached; backing off \(Int(cooldown / 60))m")
                return cached(lastSnapshot, credential)
            }
            throw UsageFetchError.http(status: status, retryAfter: retryAfter)
        } catch let error where policy.serveCachedOnAnyFailure && !Self.isCancellation(error) && lastSnapshot != nil {
            Log.write("provider", "\(tool.displayName) fetch failed, serving cached: \(type(of: error))")
            return cached(lastSnapshot!, credential)
        }
    }

    private func cached(_ snapshot: UsageSnapshot, _ credential: ToolCredential?) -> ProviderOutcome {
        var served = snapshot
        served.plan = endpoint.credentialPlan(credential) ?? snapshot.plan
        return ProviderOutcome(snapshot: served, signedIn: true, isLive: false)
    }

    /// A CLI re-login or account switch must not serve the prior account's cache or
    /// cooldown. Keeps only a one-way fingerprint, never the token itself.
    private func trackFingerprint(_ credential: ToolCredential?) {
        let next = credential.map { credential in
            SHA256.hash(data: Data(credential.accessToken.utf8)).map { String(format: "%02x", $0) }.joined()
        }
        if (fingerprintInitialized && next != fingerprint)
            || (lastSnapshot != nil && credential?.historyIdentity != nil && lastSnapshot?.historyIdentity != credential?.historyIdentity) {
            lastSnapshot = nil
            lastSuccessAt = nil
            gate?.recordSuccess()
        }
        fingerprint = next
        fingerprintInitialized = true
    }

    static func isCancellation(_ error: any Error) -> Bool {
        error is CancellationError || (error as? URLError)?.code == .cancelled
    }
}

public struct AccountUsageProvider: UsageProvider {
    public let account: ToolAccount
    private let provider: any UsageProvider

    init(account: ToolAccount, provider: any UsageProvider) {
        self.account = account
        self.provider = provider
    }

    public var tool: ToolKind { account.tool }

    public func fetch(_ interaction: FetchInteraction) async throws -> ProviderOutcome {
        try await provider.fetch(interaction)
    }
}

public enum ProviderFactory {
    private static let transport = URLSessionTransport()
    private static let locator = CLILocator()
    private static let runner = SystemProcessRunner()

    /// The production wiring: one shared transport, CLI-owned credentials, and a delegated
    /// refresh per tool. The refresh commands were verified against the real CLIs: a status
    /// command (`claude auth status`, `codex login status`) only prints cached auth and does
    /// NOT refresh; these establish a full authenticated context without spending usage.
    public static func makeDefault() -> [any UsageProvider] {
        [
            HTTPUsageProvider(
                tool: .claude,
                credentials: ClaudeCredentialSource(),
                endpoint: ClaudeEndpoint(),
                policy: .claude,
                transport: transport,
                refresher: DelegatedTokenRefresher(
                    command: CLICommand(executable: "claude", arguments: ["mcp", "list"]),
                    timeout: 15, locator: locator, runner: runner)),
            HTTPUsageProvider(
                tool: .codex,
                credentials: CodexCredentialSource(),
                endpoint: CodexEndpoint(),
                policy: .codex,
                transport: transport,
                refresher: DelegatedTokenRefresher(
                    command: CLICommand(executable: "codex", arguments: ["doctor"]),
                    timeout: 30, locator: locator, runner: runner)),
            // Cursor and Copilot need no delegated refresh: Cursor rotates its session while
            // the app runs, and gh keeps its own long-lived token. Both read once per call
            // with no throttling, like their Windows providers; failures propagate so the
            // coordinator keeps the last good snapshot.
            HTTPUsageProvider(
                tool: .cursor,
                credentials: CursorCredentialSource(),
                endpoint: CursorEndpoint(),
                policy: FetchPolicy(),
                transport: transport),
            // No endpoint or credential: reads the app's own language server, or launches one.
            AntigravityProvider(),
            HTTPUsageProvider(
                tool: .githubCopilot,
                credentials: GitHubCopilotCredentialSource(
                    tokenReader: GitHubCLITokenReader(locator: locator, runner: runner)),
                endpoint: GitHubCopilotEndpoint(),
                policy: FetchPolicy(),
                transport: transport),
        ]
    }

    public static func makeAccount(_ account: ToolAccount) -> AccountUsageProvider? {
        guard ToolAccount.validatedLabel(account.label, for: account.tool) == account.label else { return nil }
        if account.tool == .githubCopilot {
            return AccountUsageProvider(account: account, provider: HTTPUsageProvider(
                tool: account.tool,
                credentials: GitHubCopilotCredentialSource(
                    tokenReader: GitHubCLITokenReader(locator: locator, runner: runner, username: account.label),
                    useLocalFiles: false),
                endpoint: GitHubCopilotEndpoint(accountLogin: account.label), policy: FetchPolicy(), transport: transport))
        }
        guard let profile = AccountCLIProfile(account: account) else { return nil }
        let isClaude = account.tool == .claude
        let credentials: any CredentialSource = isClaude
            ? ClaudeCredentialSource(scopedConfigDirectory: profile.directory)
            : CodexCredentialSource(environment: profile.variables, scopeHistory: true)
        let endpoint: any UsageEndpoint = isClaude ? ClaudeEndpoint() : CodexEndpoint()
        return AccountUsageProvider(account: account, provider: HTTPUsageProvider(
            tool: account.tool, credentials: credentials, endpoint: endpoint,
            policy: isClaude ? .claude : .codex, transport: transport,
            refresher: DelegatedTokenRefresher(command: profile.refresh, timeout: isClaude ? 15 : 30,
                                               locator: locator, runner: runner, account: profile)))
    }
}
