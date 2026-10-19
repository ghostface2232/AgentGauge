import Foundation

/// A CLI-owned OAuth credential, held in memory only. AgentGauge never writes, refreshes,
/// or logs these; rotation stays with the official CLI.
public struct ToolCredential: Sendable, Equatable {
    public var accessToken: String
    public var accountID: String?
    public var expiresAt: Date?
    public var plan: String?

    public init(accessToken: String, accountID: String? = nil, expiresAt: Date? = nil, plan: String? = nil) {
        self.accessToken = accessToken
        self.accountID = accountID
        self.expiresAt = expiresAt
        self.plan = plan
    }
}

public enum CredentialProblem: Sendable, Equatable {
    case expired
    case noToken
    case unreadable
    case keychainDenied

    /// Whether running the CLI's own bootstrap could fix it. A denied keychain prompt or an
    /// unreadable file is not something a token refresh repairs.
    var isRefreshable: Bool { self == .expired || self == .noToken }
}

public enum CredentialRead: Sendable, Equatable {
    case available(ToolCredential)
    /// Never signed in on this Mac: a clean "no data" state, not a failure.
    case missing
    case invalid(CredentialProblem)
}

public protocol CredentialSource: Sendable {
    /// `interactive` is true for a user-initiated read, which may show the keychain prompt
    /// again after an earlier denial; background reads never re-ask.
    func read(interactive: Bool) async -> CredentialRead
}

/// Pure parsing of the CLIs' credential documents, shared by every source and the tests.
public enum CredentialParsing {
    /// Claude Code's document — the same JSON whether it sits in the macOS keychain item
    /// "Claude Code-credentials" or in `~/.claude/.credentials.json`:
    /// `{ "claudeAiOauth": { "accessToken", "expiresAt" (ms), "subscriptionType", "rateLimitTier" } }`.
    public static func claude(from data: Data, now: Date) -> CredentialRead {
        guard let root = try? JSONObject.parse(data) else { return .invalid(.unreadable) }
        guard let oauth = root.object("claudeAiOauth"),
              let token = oauth.string("accessToken"), !token.isEmpty else { return .invalid(.noToken) }
        let expiresAt = oauth.int64("expiresAt").map { Date(timeIntervalSince1970: TimeInterval($0) / 1000) }
        if let expiresAt, expiresAt <= now { return .invalid(.expired) }
        return .available(ToolCredential(
            accessToken: token,
            expiresAt: expiresAt,
            plan: claudePlan(subscriptionType: oauth.string("subscriptionType"), rateLimitTier: oauth.string("rateLimitTier"))))
    }

    /// Codex's `auth.json`: `{ "tokens": { "access_token", "account_id" } }`. There is no
    /// expiry field, so expiry comes from the access token's own JWT `exp` claim.
    public static func codex(from data: Data, now: Date) -> CredentialRead {
        guard let root = try? JSONObject.parse(data) else { return .invalid(.unreadable) }
        guard let tokens = root.object("tokens"),
              let token = tokens.string("access_token"), !token.isEmpty else { return .invalid(.noToken) }
        let expiresAt = jwtExpiry(token)
        if let expiresAt, expiresAt <= now { return .invalid(.expired) }
        return .available(ToolCredential(accessToken: token, accountID: tokens.string("account_id"), expiresAt: expiresAt))
    }

    /// Re-checks a cached credential's expiry without re-reading its source.
    static func revalidated(_ credential: ToolCredential, now: Date) -> CredentialRead {
        if let expiresAt = credential.expiresAt, expiresAt <= now { return .invalid(.expired) }
        return .available(credential)
    }

    /// The `exp` claim of a JWT payload, read without validating the signature — only the
    /// expiry is needed, not trust in the token. Nil when the value is not a decodable JWT.
    public static func jwtExpiry(_ jwt: String) -> Date? {
        let parts = jwt.split(separator: ".", omittingEmptySubsequences: false)
        guard parts.count >= 2 else { return nil }
        var base64 = parts[1].replacingOccurrences(of: "-", with: "+").replacingOccurrences(of: "_", with: "/")
        base64 += String(repeating: "=", count: (4 - base64.count % 4) % 4)
        guard let payload = Data(base64Encoded: base64),
              let claims = try? JSONObject.parse(payload),
              let seconds = claims.int64("exp") else { return nil }
        return Date(timeIntervalSince1970: TimeInterval(seconds))
    }

    public static func claudePlan(subscriptionType: String?, rateLimitTier: String?) -> String? {
        guard let type = ProviderText.normalize(subscriptionType)?.lowercased() else { return nil }
        let tier = rateLimitTier?.lowercased() ?? ""
        switch type {
        case "max" where tier.contains("20x"): return "Max 20x"
        case "max" where tier.contains("5x"): return "Max 5x"
        case "max": return "Max"
        case "pro": return "Pro"
        case "free": return "Free"
        case "team": return "Team"
        case "enterprise": return "Enterprise"
        default: return type.prefix(1).uppercased() + type.dropFirst()
        }
    }
}

/// Reads a CLI's credential file, retrying briefly on a failure that looks like the CLI
/// rewriting it. Reads are uncoordinated, so one can land inside the CLI's own token
/// rotation and see a half-written file — indistinguishable from corruption on a single
/// attempt. Absence is decided once up front: only a file that was never there is "not
/// signed in", because that empty state overwrites the last good snapshot.
struct CredentialFileReader: Sendable {
    static let attempts = 3

    func read(_ url: URL, parse: @Sendable (Data) -> CredentialRead) async -> CredentialRead {
        guard FileManager.default.fileExists(atPath: url.path) else { return .missing }
        for attempt in 1...Self.attempts {
            do {
                let result = parse(try Data(contentsOf: url))
                if result == .invalid(.unreadable), attempt < Self.attempts {
                    try? await Task.sleep(for: .milliseconds(60))
                    continue
                }
                return result
            } catch let error as CocoaError where error.code == .fileReadNoPermission {
                // Permissions are not transient; no wait fixes them.
                Log.write("auth", "Credential file not readable: permission denied")
                return .invalid(.unreadable)
            } catch {
                if attempt < Self.attempts {
                    try? await Task.sleep(for: .milliseconds(60))
                    continue
                }
                Log.write("auth", "Credential read failed after \(attempt) attempt(s): \(type(of: error))")
            }
        }
        return .invalid(.unreadable)
    }
}

/// Codex keeps its OAuth tokens in `$CODEX_HOME/auth.json` (default `~/.codex`).
public struct CodexCredentialSource: CredentialSource {
    let fileURL: URL
    let now: @Sendable () -> Date

    public init(
        environment: [String: String] = ProcessInfo.processInfo.environment,
        homeDirectory: URL = FileManager.default.homeDirectoryForCurrentUser,
        now: @escaping @Sendable () -> Date = { Date() }
    ) {
        let home = ProviderText.normalize(environment["CODEX_HOME"])
            .map { URL(fileURLWithPath: $0, isDirectory: true) }
            ?? homeDirectory.appending(path: ".codex", directoryHint: .isDirectory)
        fileURL = home.appending(path: "auth.json")
        self.now = now
    }

    public func read(interactive: Bool) async -> CredentialRead {
        let now = self.now
        return await CredentialFileReader().read(fileURL) { CredentialParsing.codex(from: $0, now: now()) }
    }
}
