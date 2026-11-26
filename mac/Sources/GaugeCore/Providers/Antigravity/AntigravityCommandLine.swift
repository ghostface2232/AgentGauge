import Foundation

/// A language server's argv and the named flags read from it. Builds before 2.35 carry the
/// CSRF token as a `--flag value` pair (Go's flag package also accepts `--flag=value`, so
/// both are read).
///
/// The token is a secret: `description` masks it, so the value is available through
/// `value(of:)` for immediate use but never leaks into a logged command line.
struct AntigravityCommandLine: CustomStringConvertible, Sendable {
    private static let sensitiveFlags: Set<String> = ["--csrf_token", "--api_key", "--extension_server_csrf_token"]

    let arguments: [String]

    init(_ arguments: [String]) {
        self.arguments = arguments
    }

    /// The value following `flag` (or after `flag=`); nil if absent or the flag is last.
    func value(of flag: String) -> String? {
        for (index, argument) in arguments.enumerated() {
            if argument == flag {
                return index + 1 < arguments.count ? arguments[index + 1] : nil
            }
            if argument.hasPrefix(flag + "=") {
                return String(argument.dropFirst(flag.count + 1))
            }
        }
        return nil
    }

    func hasFlag(_ flag: String) -> Bool {
        arguments.contains { $0 == flag || $0.hasPrefix(flag + "=") }
    }

    /// Diagnostic rendering with secret flag values masked. Never the raw token.
    var description: String {
        var parts: [String] = []
        for (index, argument) in arguments.enumerated() {
            if index > 0, Self.sensitiveFlags.contains(arguments[index - 1]) {
                parts.append("***")
            } else if let flag = Self.sensitiveFlags.first(where: { argument.hasPrefix($0 + "=") }) {
                parts.append(flag + "=***")
            } else {
                parts.append(argument.contains(" ") ? "\"\(argument)\"" : argument)
            }
        }
        return parts.joined(separator: " ")
    }
}

/// Decides whether a running process is an Antigravity language server worth talking to. The
/// binary name has varied across builds (`language_server`, `language_server_macos_arm`), so
/// the name is matched by prefix. Two further checks reject look-alikes: the executable must
/// sit inside the Antigravity bundle (when it was located), and it must carry a CSRF token —
/// without one the server answers 401 and is useless anyway.
enum AntigravityProcessMatch {
    /// Antigravity 2.35 stopped passing `--csrf_token` and hands the engine its token in this
    /// environment variable instead; older builds still use the flag, so both are read.
    static let csrfTokenEnvironmentKey = "ANTIGRAVITY_SERVER_CSRF_TOKEN"

    static func isCandidate(executablePath: String?, install: AntigravityInstall?,
                            commandLine: AntigravityCommandLine, environment: [String]?) -> Bool {
        guard let executablePath, !executablePath.isEmpty, isLanguageServerName(executablePath) else { return false }
        // Install not located: fall back to name + token rather than reject a real server.
        if let install, !install.contains(executablePath) { return false }
        return csrfToken(commandLine: commandLine, environment: environment) != nil
    }

    static func csrfToken(commandLine: AntigravityCommandLine, environment: [String]?) -> String? {
        if let token = ProviderText.normalize(commandLine.value(of: "--csrf_token")) { return token }
        let prefix = csrfTokenEnvironmentKey + "="
        return ProviderText.normalize(environment?.first { $0.hasPrefix(prefix) }.map { String($0.dropFirst(prefix.count)) })
    }

    static func isLanguageServerName(_ executablePath: String) -> Bool {
        (executablePath as NSString).lastPathComponent.lowercased().hasPrefix("language_server")
    }
}
