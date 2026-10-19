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

/// Claude Code on macOS keeps its OAuth document in the login keychain (generic password,
/// service "Claude Code-credentials"), not in `~/.claude/.credentials.json` as on Windows.
/// The file is still honoured when no keychain item exists (older installs, or
/// `CLAUDE_CONFIG_DIR` setups that store it there).
///
/// Reading another app's keychain item shows a macOS prompt ("AgentGauge wants to use your
/// confidential information…"). To keep that a one-time question, the secret is re-read
/// only when the item's modification date moves — i.e. Claude Code rotated the token —
/// because the attribute read used to detect that never prompts. A denial is remembered and
/// not re-asked by background polls; only a user-initiated refresh asks again.
public actor ClaudeCredentialSource: CredentialSource {
    public static let keychainService = "Claude Code-credentials"

    private let keychain: any KeychainReading
    private let fileURL: URL
    private let now: @Sendable () -> Date
    private var cachedItem: KeychainItem?
    private var cachedRead: CredentialRead?

    public init(
        keychain: any KeychainReading = SystemKeychain(),
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
                return CredentialParsing.revalidated(credential, now: now())
            case .invalid(.keychainDenied) where interactive:
                break
            default:
                return cachedRead
            }
        }

        let result: CredentialRead
        switch await keychain.password(service: Self.keychainService, account: item.account) {
        case .data(let data):
            result = CredentialParsing.claude(from: data, now: now())
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
}
