import Foundation
import Security

public struct KeychainItem: Sendable, Equatable {
    public var account: String?
    public var modified: Date?

    public init(account: String?, modified: Date?) {
        self.account = account
        self.modified = modified
    }
}

public enum KeychainPasswordResult: Sendable, Equatable {
    case data(Data)
    case notFound
    /// The user answered Deny (or dismissed) the keychain prompt.
    case denied
    case failed(Int32)
}

public protocol KeychainReading: Sendable {
    /// Attributes only. Reading attributes never shows the keychain access prompt.
    func newestItem(service: String) async -> KeychainItem?
    /// The secret itself. For an item another app created, macOS asks the user first.
    func password(service: String, account: String?) async -> KeychainPasswordResult
}

/// Read-only access to the login keychain through the Security framework.
public struct SystemKeychain: KeychainReading {
    public init() {}

    public func newestItem(service: String) async -> KeychainItem? {
        let query: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service,
            kSecMatchLimit as String: kSecMatchLimitAll,
            kSecReturnAttributes as String: true,
        ]
        var result: CFTypeRef?
        guard SecItemCopyMatching(query as CFDictionary, &result) == errSecSuccess,
              let items = result as? [[String: Any]] else { return nil }
        return items
            .map { KeychainItem(account: $0[kSecAttrAccount as String] as? String,
                                modified: $0[kSecAttrModificationDate as String] as? Date) }
            .max { ($0.modified ?? .distantPast) < ($1.modified ?? .distantPast) }
    }

    public func password(service: String, account: String?) async -> KeychainPasswordResult {
        // The access prompt blocks this call until the user answers, so it runs off the
        // cooperative pool rather than stalling a Swift concurrency thread for that long.
        await withCheckedContinuation { continuation in
            DispatchQueue.global(qos: .userInitiated).async {
                var query: [String: Any] = [
                    kSecClass as String: kSecClassGenericPassword,
                    kSecAttrService as String: service,
                    kSecMatchLimit as String: kSecMatchLimitOne,
                    kSecReturnData as String: true,
                ]
                if let account { query[kSecAttrAccount as String] = account }
                var result: CFTypeRef?
                let status = SecItemCopyMatching(query as CFDictionary, &result)
                switch status {
                case errSecSuccess:
                    continuation.resume(returning: (result as? Data).map(KeychainPasswordResult.data) ?? .failed(status))
                case errSecItemNotFound:
                    continuation.resume(returning: .notFound)
                case errSecUserCanceled, errSecAuthFailed, errSecInteractionNotAllowed:
                    continuation.resume(returning: .denied)
                default:
                    continuation.resume(returning: .failed(status))
                }
            }
        }
    }
}

/// Reads the secret through `/usr/bin/security`, the tool Claude Code itself writes the item
/// with, so the item's ACL already trusts it and no access prompt appears. That matters because
/// Claude Code rewrites the item and its ACL on every token refresh, which drops a
/// Security.framework "Always Allow" grant for this app (CodexBar #2380/#3798) and would
/// re-prompt after each rotation. Attributes still come from Security.framework, which never
/// prompts for them. The tool's output is the credential document: it stays in memory and is
/// never logged. Security.framework remains the fallback when the tool cannot be used.
public struct SecurityToolKeychain: KeychainReading {
    static let tool = URL(fileURLWithPath: "/usr/bin/security")
    /// Long enough for a user to answer the tool's own prompt if the ACL ever lacks it.
    static let timeout: TimeInterval = 60

    private let runner: any ProcessRunning
    private let fallback: any KeychainReading

    public init(runner: any ProcessRunning = SystemProcessRunner(), fallback: any KeychainReading = SystemKeychain()) {
        self.runner = runner
        self.fallback = fallback
    }

    public func newestItem(service: String) async -> KeychainItem? {
        await fallback.newestItem(service: service)
    }

    public func password(service: String, account: String?) async -> KeychainPasswordResult {
        var arguments = ["find-generic-password", "-s", service]
        if let account { arguments += ["-a", account] }
        arguments.append("-w")
        let result = await runner.run(Self.tool, arguments: arguments, environment: [:],
                                      timeout: Self.timeout, captureOutput: true)
        if let interpreted = Self.interpret(result) { return interpreted }
        return await fallback.password(service: service, account: account)
    }

    /// Nil means "use the fallback". The tool exits with the low byte of the OSStatus.
    static func interpret(_ result: ProcessResult) -> KeychainPasswordResult? {
        guard result.launched else {
            Log.write("auth", "security tool did not launch; reading the keychain through Security.framework")
            return nil
        }
        // A timeout means the tool's own prompt went unanswered; falling back would stack a
        // second prompt on top, so it counts as a denial (re-asked only by a user refresh).
        if result.timedOut { return .denied }
        switch result.exitCode {
        case 0:
            if let data = result.output.flatMap(decodePassword) { return .data(data) }
            Log.write("auth", "security tool output was not a credential document; reading through Security.framework")
            return nil
        case 44: // errSecItemNotFound
            return .notFound
        case 128, 51, 36: // errSecUserCanceled, errSecAuthFailed, errSecInteractionNotAllowed
            return .denied
        default:
            Log.write("auth", "security tool exited \(result.exitCode.map { String($0) } ?? "-"); reading through Security.framework")
            return nil
        }
    }

    /// `security -w` prints the secret followed by a newline, or as hex when the bytes are not
    /// printable text (reported on macOS 26, ClaudeBar #255).
    static func decodePassword(_ output: Data) -> Data? {
        guard let text = String(data: output, encoding: .utf8)?.trimmingCharacters(in: .whitespacesAndNewlines),
              !text.isEmpty else { return nil }
        if text.hasPrefix("{") { return Data(text.utf8) }
        guard text.count.isMultiple(of: 2), text.allSatisfy(\.isHexDigit) else { return nil }
        var bytes = [UInt8]()
        bytes.reserveCapacity(text.count / 2)
        var index = text.startIndex
        while index < text.endIndex {
            let next = text.index(index, offsetBy: 2)
            guard let byte = UInt8(text[index..<next], radix: 16) else { return nil }
            bytes.append(byte)
            index = next
        }
        let data = Data(bytes)
        return data.first == UInt8(ascii: "{") ? data : nil
    }
}

/// Claude Code on macOS keeps its OAuth document in the login keychain (generic password,
/// service "Claude Code-credentials"), not in `~/.claude/.credentials.json` as on Windows.
/// The file is still honoured when no keychain item exists (older installs, or
/// `CLAUDE_CONFIG_DIR` setups that store it there).
///
/// The secret is read through `SecurityToolKeychain`, which normally never prompts; if it has
/// to fall back to Security.framework, reading another app's item does prompt. Either way the
/// secret is re-read only when the item's modification date moves (Claude Code rotated the
/// token or wrote an MCP entry), because the attribute read used to detect that never
/// prompts. A denial is remembered and not re-asked by background polls; only a
/// user-initiated refresh asks again.
public actor ClaudeCredentialSource: CredentialSource {
    public static let keychainService = "Claude Code-credentials"

    private let keychain: any KeychainReading
    private let fileURL: URL
    private let now: @Sendable () -> Date
    private var cachedItem: KeychainItem?
    private var cachedRead: CredentialRead?
    /// The document behind a cached `.available` read. Expiry is re-judged from it rather than
    /// from the credential, because whether an expired token can still be refreshed depends
    /// on fields the credential does not carry.
    private var cachedDocument: Data?

    public init(
        keychain: any KeychainReading = SecurityToolKeychain(),
        environment: [String: String] = ProcessInfo.processInfo.environment,
        homeDirectory: URL = FileManager.default.homeDirectoryForCurrentUser,
        now: @escaping @Sendable () -> Date = { Date() }
    ) {
        self.keychain = keychain
        let configDirectory = ProviderText.normalize(environment["CLAUDE_CONFIG_DIR"])
            .map { URL(fileURLWithPath: $0, isDirectory: true) }
            ?? homeDirectory.appending(path: ".claude", directoryHint: .isDirectory)
        fileURL = configDirectory.appending(path: ".credentials.json")
        self.now = now
    }

    public func read(interactive: Bool) async -> CredentialRead {
        if let item = await keychain.newestItem(service: Self.keychainService) {
            return await readKeychain(item, interactive: interactive)
        }
        cachedItem = nil
        cachedRead = nil
        let now = self.now
        return await CredentialFileReader().read(fileURL) { CredentialParsing.claude(from: $0, now: now()) }
    }

    private func readKeychain(_ item: KeychainItem, interactive: Bool) async -> CredentialRead {
        // An item without a modification date cannot prove it is unchanged, so it is re-read.
        if item.modified != nil, item == cachedItem, let cachedRead {
            switch cachedRead {
            case .available(let credential):
                guard let cachedDocument else { return CredentialParsing.revalidated(credential, now: now()) }
                let result = parse(cachedDocument)
                // Once expired the outcome cannot change until the item does, so it is cached
                // (and the unrefreshable case logged) only once.
                if case .invalid = result { self.cachedRead = result }
                return result
            case .invalid(.keychainDenied) where interactive:
                break
            default:
                return cachedRead
            }
        }

        let result: CredentialRead
        cachedDocument = nil
        switch await keychain.password(service: Self.keychainService, account: item.account) {
        case .data(let data):
            result = parse(data)
            if case .available = result { cachedDocument = data }
        case .notFound:
            result = .missing
        case .denied:
            result = .invalid(.keychainDenied)
        case .failed(let status):
            Log.write("auth", "Claude keychain read failed: OSStatus \(status)")
            result = .invalid(.unreadable)
        }
        cachedItem = item
        cachedRead = result
        return result
    }

    private func parse(_ document: Data) -> CredentialRead {
        let result = CredentialParsing.claude(from: document, now: now())
        if result == .invalid(.expiredUnrefreshable) {
            Log.write("auth", "Claude keychain token is expired and lacks the fields Claude Code needs to refresh it; sign-in required")
        }
        return result
    }
}
