import Foundation

/// The same environment is used for sign-in, reading credentials and delegated refresh.
public struct AccountCLIProfile: Sendable {
    public let directory: URL
    public let signIn: CLICommand
    let refresh: CLICommand
    public let variables: [String: String]
    public let clearedVariables: [String]

    public init?(account: ToolAccount) {
        let directory = UsageCacheStore.defaultDirectory().appending(path: "accounts/\(account.id.uuidString)",
                                                                    directoryHint: .isDirectory)
        self.directory = directory
        switch account.tool {
        case .claude:
            signIn = CLICommand(executable: "claude", arguments: ["auth", "login"])
            refresh = CLICommand(executable: "claude", arguments: ["mcp", "list"])
            variables = ["CLAUDE_CONFIG_DIR": directory.path]
            clearedVariables = ["CLAUDE_SECURESTORAGE_CONFIG_DIR", "CLAUDE_CODE_OAUTH_TOKEN",
                                "CLAUDE_CODE_OAUTH_TOKEN_FILE", "CLAUDE_CODE_OAUTH_TOKEN_FD",
                                "ANTHROPIC_AUTH_TOKEN", "ANTHROPIC_API_KEY"]
        case .codex:
            let fileStorage = ["-c", "cli_auth_credentials_store=file"]
            signIn = CLICommand(executable: "codex", arguments: ["login"] + fileStorage)
            refresh = CLICommand(executable: "codex", arguments: fileStorage + ["doctor"])
            variables = ["CODEX_HOME": directory.path]
            clearedVariables = ["CODEX_ACCESS_TOKEN", "CODEX_API_KEY", "OPENAI_API_KEY"]
        default:
            return nil
        }
    }

    func applying(to inherited: [String: String]) -> [String: String] {
        var environment = inherited
        clearedVariables.forEach { environment.removeValue(forKey: $0) }
        environment.merge(variables) { _, scoped in scoped }
        return environment
    }
}
