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
    static let url = URL(string: "https://api.anthropic.com/api/oauth/usage")!
    // The endpoint buckets the claude-code agent less aggressively than arbitrary ones —
    // the one place a known client's UA is matched, for interop, not deception.
    static let userAgent = "claude-code/2.1.179"

    public init() {}

    public func fetch(_ credential: ToolCredential, transport: any HTTPTransport, now: Date) async throws -> UsagePayload {
        var request = URLRequest(url: Self.url)
        request.setValue("Bearer \(credential.accessToken)", forHTTPHeaderField: "Authorization")
        request.setValue("oauth-2025-04-20", forHTTPHeaderField: "anthropic-beta")
        request.setValue(Self.userAgent, forHTTPHeaderField: "User-Agent")
        request.setValue("application/json", forHTTPHeaderField: "Accept")
        let (data, response) = try await transport.send(request)
        try HTTPStatus.ensureSuccess(response, now: now)
        return UsagePayload(plan: credential.plan, windows: try ClaudeUsageParser.windows(from: data))
    }

    public func credentialPlan(_ credential: ToolCredential?) -> String? { credential?.plan }
}

public struct CodexEndpoint: UsageEndpoint {
    static let url = URL(string: "https://chatgpt.com/backend-api/wham/usage")!

    public init() {}

    public func fetch(_ credential: ToolCredential, transport: any HTTPTransport, now: Date) async throws -> UsagePayload {
        var request = URLRequest(url: Self.url)
        request.setValue("Bearer \(credential.accessToken)", forHTTPHeaderField: "Authorization")
        request.setValue("Gauge/1.0", forHTTPHeaderField: "User-Agent")
        request.setValue("application/json", forHTTPHeaderField: "Accept")
        if let accountID = credential.accountID, !accountID.isEmpty {
            request.setValue(accountID, forHTTPHeaderField: "ChatGPT-Account-Id")
        }
        let (data, response) = try await transport.send(request)
        try HTTPStatus.ensureSuccess(response, now: now)
        return try CodexUsageParser.payload(from: data)
    }

    public func credentialPlan(_ credential: ToolCredential?) -> String? { nil }
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
        if cachesSnapshots { trackFingerprint(credential) }

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
            let snapshot = UsageSnapshot(
                tool: tool, plan: payload.plan, windows: payload.windows,
                resetCredits: payload.resetCredits, capturedAt: wallClock())
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
        if fingerprintInitialized && next != fingerprint {
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

public enum ProviderFactory {
    /// The production wiring: one shared transport, CLI-owned credentials, and a delegated
    /// refresh per tool. The refresh commands were verified against the real CLIs: a status
    /// command (`claude auth status`, `codex login status`) only prints cached auth and does
    /// NOT refresh; these establish a full authenticated context without spending usage.
    public static func makeDefault() -> [any UsageProvider] {
        let transport = URLSessionTransport()
        let locator = CLILocator()
        let runner = SystemProcessRunner()
        return [
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
            HTTPUsageProvider(
                tool: .githubCopilot,
                credentials: GitHubCopilotCredentialSource(
                    tokenReader: GitHubCLITokenReader(locator: locator, runner: runner)),
                endpoint: GitHubCopilotEndpoint(),
                policy: FetchPolicy(),
                transport: transport),
        ]
    }
}
