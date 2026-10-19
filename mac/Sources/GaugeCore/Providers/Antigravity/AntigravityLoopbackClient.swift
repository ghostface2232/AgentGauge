import Foundation

/// One Connect call's outcome: the HTTP status and the raw body.
struct AntigravityConnectResponse: Sendable {
    let status: Int
    let body: Data
}

/// Sends one Connect call to a loopback port. A protocol so the client's method/port fallback
/// is testable without a live engine.
protocol AntigravityConnectTransport: Sendable {
    func post(port: Int, method: String, body: Data, csrfToken: String) async throws -> AntigravityConnectResponse
}

/// The usage read from one engine: the raw quota JSON plus the best-effort plan label, from
/// two separate calls (`RetrieveUserQuotaSummary`, `GetUserStatus`). A missing plan never
/// invalidates the quota.
struct AntigravityReading: Sendable, Equatable {
    let quotaJSON: Data
    let plan: String?
}

/// Talks to a running language server over its loopback Connect API (JSON over HTTP: a POST
/// to `/exa.language_server_pb.LanguageServerService/<Method>` with the protocol-version and
/// CSRF headers). The engine opens two loopback ports on random numbers — HTTPS and plaintext
/// HTTP (which answers an HTTPS request with a failed handshake) — so each port is tried until
/// one completes the exchange, and the quota method names are probed since they differ
/// across builds.
struct AntigravityLoopbackClient: Sendable {
    static let servicePath = "/exa.language_server_pb.LanguageServerService/"
    static let quotaRequestBody = Data(#"{"forceRefresh":true}"#.utf8)
    // RetrieveUserQuotaSummary is current; RetrieveUserQuota is the older name (404 on new builds).
    static let quotaMethods = ["RetrieveUserQuotaSummary", "RetrieveUserQuota"]

    let transport: any AntigravityConnectTransport

    init(transport: any AntigravityConnectTransport = AntigravityURLSessionTransport()) {
        self.transport = transport
    }

    static func requestURL(port: Int, method: String) -> URL? {
        guard (1...65535).contains(port) else { return nil }
        return URL(string: "https://127.0.0.1:\(port)\(servicePath)\(method)")
    }

    /// The quota (and best-effort plan) from the first port that answers 200, or nil if none
    /// does. Cancellation propagates; a port that fails at the transport level is skipped.
    func reading(ports: [Int], csrfToken: String) async throws -> AntigravityReading? {
        for port in ports {
            do {
                if let quota = try await quota(port: port, csrfToken: csrfToken) {
                    return AntigravityReading(quotaJSON: quota, plan: try await plan(port: port, csrfToken: csrfToken))
                }
            } catch let error where !Self.isCancellation(error) {
                // The plaintext port (failed handshake) or a port that closed: try the next one.
            }
        }
        return nil
    }

    private func quota(port: Int, csrfToken: String) async throws -> Data? {
        for method in Self.quotaMethods {
            let response = try await transport.post(port: port, method: method, body: Self.quotaRequestBody, csrfToken: csrfToken)
            if response.status == 200 { return response.body }
            // 404 means this build names the method differently; any other status (401 for a
            // wrong token, 5xx while warming) is a real failure, not a naming mismatch.
            if response.status != 404 { return nil }
        }
        return nil
    }

    private func plan(port: Int, csrfToken: String) async throws -> String? {
        do {
            let response = try await transport.post(port: port, method: "GetUserStatus", body: Data("{}".utf8), csrfToken: csrfToken)
            return response.status == 200 ? AntigravityUserStatus.plan(from: response.body) : nil
        } catch let error where !Self.isCancellation(error) {
            return nil
        }
    }

    static func isCancellation(_ error: any Error) -> Bool {
        error is CancellationError || (error as? URLError)?.code == .cancelled
    }
}

/// The production transport: its own ephemeral URLSession whose delegate accepts the engine's
/// self-signed certificate ONLY for the `127.0.0.1` literal. The app's shared transport is
/// never weakened, proxies are bypassed (a configured system proxy must not see the CSRF
/// token), and redirects are refused so the token cannot be carried off-machine.
final class AntigravityURLSessionTransport: AntigravityConnectTransport, @unchecked Sendable {
    private let session: URLSession

    init(timeout: TimeInterval = 5) {
        let configuration = URLSessionConfiguration.ephemeral
        configuration.timeoutIntervalForRequest = timeout
        configuration.timeoutIntervalForResource = timeout * 2
        configuration.requestCachePolicy = .reloadIgnoringLocalCacheData
        configuration.urlCache = nil
        configuration.httpCookieStorage = nil
        configuration.httpShouldSetCookies = false
        configuration.connectionProxyDictionary = [:]
        session = URLSession(configuration: configuration, delegate: AntigravityLoopbackTrust(), delegateQueue: nil)
    }

    deinit {
        session.finishTasksAndInvalidate()
    }

    func post(port: Int, method: String, body: Data, csrfToken: String) async throws -> AntigravityConnectResponse {
        guard let url = AntigravityLoopbackClient.requestURL(port: port, method: method) else {
            throw URLError(.badURL)
        }
        var request = URLRequest(url: url)
        request.httpMethod = "POST"
        request.httpBody = body
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        request.setValue("1", forHTTPHeaderField: "Connect-Protocol-Version")
        request.setValue(csrfToken, forHTTPHeaderField: "X-Codeium-Csrf-Token")
        let (data, response) = try await session.data(for: request)
        guard let http = response as? HTTPURLResponse else { throw URLError(.badServerResponse) }
        return AntigravityConnectResponse(status: http.statusCode, body: data)
    }
}

/// The narrow TLS exception for the engine's self-signed certificate. Normal validation fails
/// for it, so trust is granted — but only when the challenge's host AND the task's URL are the
/// loopback IP literal `127.0.0.1`. "localhost", other 127/8 addresses and every off-machine
/// host get default handling, so this can never apply to a real destination.
final class AntigravityLoopbackTrust: NSObject, URLSessionTaskDelegate, Sendable {
    static func isTrustedLoopback(_ url: URL?) -> Bool {
        guard let url, url.scheme?.lowercased() == "https" else { return false }
        return url.host == "127.0.0.1"
    }

    func urlSession(
        _ session: URLSession,
        task: URLSessionTask,
        didReceive challenge: URLAuthenticationChallenge
    ) async -> (URLSession.AuthChallengeDisposition, URLCredential?) {
        let space = challenge.protectionSpace
        guard space.authenticationMethod == NSURLAuthenticationMethodServerTrust,
              space.host == "127.0.0.1",
              Self.isTrustedLoopback(task.currentRequest?.url ?? task.originalRequest?.url),
              let trust = space.serverTrust else {
            return (.performDefaultHandling, nil)
        }
        return (.useCredential, URLCredential(trust: trust))
    }

    func urlSession(
        _ session: URLSession,
        task: URLSessionTask,
        willPerformHTTPRedirection response: HTTPURLResponse,
        newRequest request: URLRequest
    ) async -> URLRequest? {
        nil
    }
}
