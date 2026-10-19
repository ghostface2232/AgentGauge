import Foundation

public enum UsageFetchError: Error, Sendable, Equatable {
    /// The token is expired or the server rejected it even after a delegated refresh.
    case authenticationRequired
    /// The credential exists but could not be read (keychain denied, unreadable file).
    case credentialUnreadable(CredentialProblem)
    /// A non-2xx response; `retryAfter` is the server's Retry-After, kept for the cooldown.
    case http(status: Int, retryAfter: TimeInterval?)
    /// Inside a rate-limit cooldown with nothing cached: failed fast without a network call.
    case cooldown
    case invalidResponse

    var isAuthRejection: Bool {
        if case .http(let status, _) = self { return status == 401 || status == 403 }
        return false
    }
}

/// Who asked for a fetch. Only the rate-limit cooldown distinguishes them: a user-initiated
/// refresh always gets a real attempt, a background one is served from cache.
public enum FetchInteraction: Sendable {
    case background
    case user
}

/// Doubles from the base per consecutive failure, capped at the maximum. Used where the
/// endpoint rate-limits without a Retry-After (Claude's sends none).
public struct BackoffPolicy: Sendable, Equatable {
    public let base: TimeInterval
    public let maximum: TimeInterval

    public init(base: TimeInterval, maximum: TimeInterval) {
        self.base = base
        self.maximum = maximum
    }

    public func cooldown(forAttempt consecutiveFailures: Int) -> TimeInterval {
        let exponent = Double(min(max(consecutiveFailures - 1, 0), 62))
        return min(maximum, base * pow(2, exponent))
    }
}

/// Shared rate-limit cooldown, modeled on CodexBar's gate (and the Windows app's
/// `RateLimitGate`). A retryable status arms a cooldown that holds back background fetches;
/// the server's Retry-After wins (clamped to an hour so a bogus header cannot freeze
/// refreshes), else the backoff escalates. A new failure only ever extends a standing block.
public struct RateLimitGate: Sendable {
    static let maximumRetryAfter: TimeInterval = 3600

    public let backoff: BackoffPolicy
    public private(set) var consecutiveFailures = 0
    private var cooldownStart: TimeInterval?
    private var cooldownDuration: TimeInterval = 0

    public init(backoff: BackoffPolicy) {
        self.backoff = backoff
    }

    /// The request was fine; the server just couldn't serve it now. 401/403 are excluded —
    /// they have their own delegated-refresh path and waiting would not fix them.
    public static func isRetryable(_ status: Int) -> Bool {
        [408, 429, 500, 502, 503, 504].contains(status)
    }

    public func shouldBlock(_ interaction: FetchInteraction, now: TimeInterval) -> Bool {
        interaction == .background && inCooldown(now: now)
    }

    public func inCooldown(now: TimeInterval) -> Bool {
        guard let cooldownStart else { return false }
        return now - cooldownStart < cooldownDuration
    }

    @discardableResult
    public mutating func recordFailure(retryAfter: TimeInterval?, now: TimeInterval) -> TimeInterval {
        consecutiveFailures += 1
        let candidate: TimeInterval
        if let retryAfter, retryAfter > 0 {
            candidate = min(retryAfter, Self.maximumRetryAfter)
        } else {
            candidate = backoff.cooldown(forAttempt: consecutiveFailures)
        }
        let remaining = cooldownStart.map { cooldownDuration - (now - $0) } ?? 0
        cooldownStart = now
        cooldownDuration = max(candidate, remaining)
        return cooldownDuration
    }

    public mutating func recordSuccess() {
        consecutiveFailures = 0
        cooldownStart = nil
        cooldownDuration = 0
    }
}

/// How a provider's shared fetch layer throttles and degrades.
public struct FetchPolicy: Sendable {
    /// Happy-path network cap: within this interval of a success the cached snapshot is
    /// served without a call. A COST cap, so even a user-initiated refresh does not pierce it.
    public var minimumFetchInterval: TimeInterval = 0
    /// Non-nil arms the shared cooldown gate for retryable statuses.
    public var retryBackoff: BackoffPolicy?
    /// Serve the cache on any non-auth failure, not just retryable statuses.
    public var serveCachedOnAnyFailure = false

    public init(minimumFetchInterval: TimeInterval = 0, retryBackoff: BackoffPolicy? = nil, serveCachedOnAnyFailure: Bool = false) {
        self.minimumFetchInterval = minimumFetchInterval
        self.retryBackoff = retryBackoff
        self.serveCachedOnAnyFailure = serveCachedOnAnyFailure
    }

    /// Claude's `/api/oauth/usage` allows ~3 reads in a short window, then 429s with a
    /// penalty cooldown and no Retry-After, on a bucket shared per account/IP — over-polling
    /// also starves the real CLI. So: one network read per 5 minutes, 2→4→…→30-minute
    /// backoff, and the last good value kept on any failure.
    public static let claude = FetchPolicy(
        minimumFetchInterval: 300,
        retryBackoff: BackoffPolicy(base: 120, maximum: 1800),
        serveCachedOnAnyFailure: true)

    /// Codex's endpoint is the one its CLI polls every 60s, so no happy-path cap; retryable
    /// statuses still arm the gate so a tightening on OpenAI's side is not hammered.
    public static let codex = FetchPolicy(retryBackoff: BackoffPolicy(base: 120, maximum: 1800))
}

public protocol HTTPTransport: Sendable {
    func send(_ request: URLRequest) async throws -> (Data, HTTPURLResponse)
}

public struct URLSessionTransport: HTTPTransport {
    private let session: URLSession

    public init(session: URLSession? = nil) {
        if let session {
            self.session = session
        } else {
            // Ephemeral: no cookie or cache persistence for credential-bearing requests.
            let configuration = URLSessionConfiguration.ephemeral
            configuration.timeoutIntervalForRequest = 20
            configuration.timeoutIntervalForResource = 30
            configuration.requestCachePolicy = .reloadIgnoringLocalCacheData
            self.session = URLSession(configuration: configuration)
        }
    }

    public func send(_ request: URLRequest) async throws -> (Data, HTTPURLResponse) {
        let (data, response) = try await session.data(for: request)
        guard let http = response as? HTTPURLResponse else { throw UsageFetchError.invalidResponse }
        return (data, http)
    }
}

enum HTTPStatus {
    /// Throws a status error that keeps the response's Retry-After for the cooldown.
    static func ensureSuccess(_ response: HTTPURLResponse, now: Date) throws {
        guard !(200..<300).contains(response.statusCode) else { return }
        throw UsageFetchError.http(status: response.statusCode, retryAfter: retryAfter(response, now: now))
    }

    static func retryAfter(_ response: HTTPURLResponse, now: Date) -> TimeInterval? {
        guard let value = response.value(forHTTPHeaderField: "Retry-After")?
            .trimmingCharacters(in: .whitespaces), !value.isEmpty else { return nil }
        if let seconds = TimeInterval(value) { return max(seconds, 0) }
        let formatter = DateFormatter()
        formatter.locale = Locale(identifier: "en_US_POSIX")
        formatter.timeZone = TimeZone(secondsFromGMT: 0)
        formatter.dateFormat = "EEE, dd MMM yyyy HH:mm:ss zzz"
        guard let date = formatter.date(from: value) else { return nil }
        return max(date.timeIntervalSince(now), 0)
    }
}
