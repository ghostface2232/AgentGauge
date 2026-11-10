import Foundation

public protocol GitHubTokenReading: Sendable {
    /// The gh CLI's OAuth token for github.com, or nil when gh is missing, signed out, or fails.
    func token() async throws -> String?
}

/// Reads the token with `gh auth token`. gh owns the token's storage (its keychain item or
/// hosts.yml) and its refresh, so this works wherever gh is signed in and cannot disturb
/// gh's own login. The output is the secret itself: kept in memory only, never logged.
public struct GitHubCLITokenReader: GitHubTokenReading {
    static let timeout: TimeInterval = 10

    private let locator: any CLILocating
    private let runner: any ProcessRunning
    private let username: String?

    public init(locator: any CLILocating, runner: any ProcessRunning, username: String? = nil) {
        self.locator = locator
        self.runner = runner
        self.username = username
    }

    public func token() async throws -> String? {
        guard let executable = await locator.locate("gh") else { return nil }
        var environment = await locator.environment(for: executable)
        var arguments = ["auth", "token", "--hostname", "github.com"]
        if let username {
            arguments += ["--user", username]
            environment.removeValue(forKey: "GH_TOKEN")
            environment.removeValue(forKey: "GITHUB_TOKEN")
        }
        let result = await runner.run(
            executable, arguments: arguments,
            environment: environment, timeout: Self.timeout, captureOutput: true)
        if username != nil && (!result.launched || result.timedOut) { throw ReadError.unavailable }
        // Only a clean exit yields a token; a non-zero exit means gh is signed out.
        guard result.exitCode == 0, !result.timedOut, let output = result.output else { return nil }
        return ProviderText.normalize(String(data: output, encoding: .utf8))
    }

    private enum ReadError: Error { case unavailable }
}

/// Copilot's GitHub OAuth token has no single fixed home, so the sources a Copilot user is
/// likely to have are tried in order: (1) the gh CLI, which also backs the standalone
/// Copilot CLI; (2) a `github-copilot` `apps.json`/`hosts.json` (`oauth_token`) under
/// `$XDG_CONFIG_HOME` or `~/.config`, written by copilot.vim/Neovim and older editor
/// extensions. A setup that keeps the token only in an editor's encrypted secret store
/// exposes none and reads as signed out. Read-only and in memory; never written or refreshed.
public struct GitHubCopilotCredentialSource: CredentialSource {
    let tokenReader: any GitHubTokenReading
    let configDirectories: [URL]

    public init(
        tokenReader: any GitHubTokenReading,
        environment: [String: String] = ProcessInfo.processInfo.environment,
        homeDirectory: URL = FileManager.default.homeDirectoryForCurrentUser,
        useLocalFiles: Bool = true
    ) {
        self.tokenReader = tokenReader
        guard useLocalFiles else {
            configDirectories = []
            return
        }
        var directories: [URL] = []
        if let xdg = ProviderText.normalize(environment["XDG_CONFIG_HOME"]) {
            directories.append(URL(fileURLWithPath: xdg, isDirectory: true).appending(path: "github-copilot", directoryHint: .isDirectory))
        }
        directories.append(homeDirectory.appending(path: ".config/github-copilot", directoryHint: .isDirectory))
        var seen = Set<String>()
        configDirectories = directories.filter { seen.insert($0.standardizedFileURL.path).inserted }
    }

    public func read(interactive: Bool) async -> CredentialRead {
        do {
            if let token = try await tokenReader.token() {
                return .available(ToolCredential(accessToken: token))
            }
        } catch {
            return .invalid(.unreadable)
        }
        if let token = tokenFromFiles() {
            return .available(ToolCredential(accessToken: token))
        }
        return .missing
    }

    private func tokenFromFiles() -> String? {
        // apps.json is the current name, hosts.json the legacy one; both share the schema.
        for directory in configDirectories {
            for name in ["apps.json", "hosts.json"] {
                let url = directory.appending(path: name)
                guard FileManager.default.fileExists(atPath: url.path) else { continue }
                do {
                    if let token = Self.oauthToken(in: try Data(contentsOf: url)) { return token }
                } catch {
                    // Never log the file contents or the token; the type is enough.
                    Log.write("auth", "Copilot token file read failed: \(type(of: error))")
                }
            }
        }
        return nil
    }

    /// The file maps an app/host key (e.g. `"github.com:Iv1.abc"`) to an object carrying an
    /// `oauth_token`. A github.com entry wins; otherwise the first token by key order.
    static func oauthToken(in data: Data) -> String? {
        guard let root = try? JSONObject.parse(data) else { return nil }
        var fallback: String?
        for key in root.storage.keys.sorted() {
            guard let token = ProviderText.normalize(root.object(key)?.string("oauth_token")) else { continue }
            if key.lowercased().contains("github.com") { return token }
            fallback = fallback ?? token
        }
        return fallback
    }
}
