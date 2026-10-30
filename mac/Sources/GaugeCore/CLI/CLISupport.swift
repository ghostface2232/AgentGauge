import Darwin
import Foundation

public struct ProcessResult: Sendable, Equatable {
    public var exitCode: Int32?
    public var timedOut: Bool
    public var launched: Bool
    public var output: Data?

    static let launchFailed = ProcessResult(exitCode: nil, timedOut: false, launched: false, output: nil)
}

public protocol ProcessRunning: Sendable {
    func run(_ executable: URL, arguments: [String], environment: [String: String],
             timeout: TimeInterval, captureOutput: Bool) async -> ProcessResult
}

/// Runs a process headlessly: stdin is /dev/null (so a CLI never waits on a TTY) and stderr
/// is discarded. Output is captured only when asked (the login-shell PATH probe, and
/// `gh auth token`, whose output is the token and stays in memory); a CLI's own output
/// carries account information and is never logged.
public struct SystemProcessRunner: ProcessRunning {
    public init() {}

    public func run(_ executable: URL, arguments: [String], environment: [String: String],
                    timeout: TimeInterval, captureOutput: Bool) async -> ProcessResult {
        await withCheckedContinuation { continuation in
            let process = Process()
            process.executableURL = executable
            process.arguments = arguments
            process.environment = environment
            process.standardInput = FileHandle.nullDevice
            process.standardError = FileHandle.nullDevice
            let pipe: Pipe? = captureOutput ? Pipe() : nil
            process.standardOutput = pipe ?? FileHandle.nullDevice
            let state = RunState()
            // Drained while the process runs: output larger than the pipe buffer (the keychain
            // document grows with every MCP OAuth entry) would otherwise block the writer and
            // only end at the timeout.
            let drained = DispatchGroup()
            let collected = OutputBox()

            process.terminationHandler = { finished in
                drained.wait()
                let output = pipe == nil ? nil : collected.data
                state.finishOnce {
                    continuation.resume(returning: ProcessResult(
                        exitCode: finished.terminationStatus, timedOut: state.timedOut, launched: true, output: output))
                }
            }
            do {
                try process.run()
            } catch {
                state.finishOnce { continuation.resume(returning: .launchFailed) }
                return
            }
            if let pipe {
                drained.enter()
                DispatchQueue.global().async {
                    collected.data = pipe.fileHandleForReading.readDataToEndOfFile()
                    drained.leave()
                }
            }
            DispatchQueue.global().asyncAfter(deadline: .now() + timeout) {
                guard process.isRunning else { return }
                state.markTimedOut()
                process.terminate()
                DispatchQueue.global().asyncAfter(deadline: .now() + 2) {
                    if process.isRunning { kill(process.processIdentifier, SIGKILL) }
                }
            }
        }
    }

    private final class OutputBox: @unchecked Sendable {
        private let lock = NSLock()
        private var stored = Data()
        var data: Data {
            get { lock.withLock { stored } }
            set { lock.withLock { stored = newValue } }
        }
    }

    private final class RunState: @unchecked Sendable {
        private let lock = NSLock()
        private var finished = false
        private var didTimeOut = false

        var timedOut: Bool { lock.withLock { didTimeOut } }

        func markTimedOut() { lock.withLock { didTimeOut = true } }

        func finishOnce(_ body: () -> Void) {
            let first = lock.withLock { () -> Bool in
                defer { finished = true }
                return !finished
            }
            if first { body() }
        }
    }
}

public protocol CLILocating: Sendable {
    func locate(_ name: String) async -> URL?
    /// The environment to launch a located CLI with. The CLIs are Node scripts
    /// (`#!/usr/bin/env node`), so PATH must reach node too.
    func environment(for executable: URL) async -> [String: String]
}

/// Finds the official CLIs. An app launched from Finder or at login does not inherit the
/// shell's PATH, so the usual install locations are checked first and the user's login
/// shell PATH (where nvm/asdf/Homebrew put things) is asked once as a fallback.
///
/// Last comes the copy a desktop app ships, so a desktop-only install can still sign in and
/// run the delegated refresh: the Claude desktop app unpacks the real `claude` binary under
/// its Application Support folder, and the Codex app bundles `codex` and shares
/// `~/.codex/auth.json` with the CLI. The desktop apps' own token stores are never read.
public actor CLILocator: CLILocating {
    private let runner: any ProcessRunning
    private let homeDirectory: URL
    private var loginShellPath: [String]?

    public init(runner: any ProcessRunning = SystemProcessRunner(),
                homeDirectory: URL = FileManager.default.homeDirectoryForCurrentUser) {
        self.runner = runner
        self.homeDirectory = homeDirectory
    }

    public func locate(_ name: String) async -> URL? {
        if let found = Self.firstExecutable(name, in: wellKnownDirectories()) { return found }
        if let found = Self.firstExecutable(name, in: await shellPath()) { return found }
        return bundledExecutable(name)
    }

    private func bundledExecutable(_ name: String) -> URL? {
        switch name {
        case "claude":
            return Self.newestClaudeBinary(
                in: homeDirectory.appending(path: "Library/Application Support/Claude/claude-code"))
        case "codex":
            return Self.bundledCodexBinary(
                inApplications: ["/Applications", homeDirectory.appending(path: "Applications").path])
        default:
            return nil
        }
    }

    /// The Codex desktop app installs as `ChatGPT.app` (bundle id `com.openai.codex`) and keeps
    /// its CLI at `Contents/Resources/codex-cli/bin/codex`, beside the `rg` it expects in
    /// `codex-cli/codex-path`. `Codex.app` and a bare `Resources/codex` cover other packagings.
    static func bundledCodexBinary(inApplications roots: [String]) -> URL? {
        let bundles = roots.flatMap { root in ["ChatGPT.app", "Codex.app"].map { "\(root)/\($0)/Contents/Resources" } }
        return firstExecutable("codex", in: bundles.flatMap { ["\($0)/codex-cli/bin", $0] })
    }

    /// The desktop app unpacks its CLI per version (`claude-code/<version>/<build hash>/…`) and
    /// can keep an older one beside the current, so the highest version wins. The binary sits in
    /// a `claude.app` wrapper (`claude.app/Contents/MacOS/claude`, observed with 2.1.309) either
    /// one build-hash folder down or directly in the version folder, or bare in either place.
    static func newestClaudeBinary(in root: URL) -> URL? {
        let fileManager = FileManager.default
        guard let versions = try? fileManager.contentsOfDirectory(atPath: root.path) else { return nil }
        let ordered = versions
            .filter { $0.first?.isNumber == true }
            .sorted { $0.compare($1, options: .numeric) == .orderedDescending }
        for version in ordered {
            let directory = root.appending(path: version)
            var folders = [directory]
            if let builds = try? fileManager.contentsOfDirectory(atPath: directory.path) {
                folders += builds.sorted().map { directory.appending(path: $0) }
            }
            let candidates = folders.flatMap { [$0.appending(path: "claude.app/Contents/MacOS").path, $0.path] }
            if let found = firstExecutable("claude", in: candidates) { return found }
        }
        return nil
    }

    private static func firstExecutable(_ name: String, in directories: [String]) -> URL? {
        for directory in directories {
            let candidate = URL(fileURLWithPath: directory, isDirectory: true).appending(path: name)
            // isExecutableFile is also true for a searchable directory of the same name.
            var isDirectory: ObjCBool = false
            if FileManager.default.fileExists(atPath: candidate.path, isDirectory: &isDirectory),
               !isDirectory.boolValue, FileManager.default.isExecutableFile(atPath: candidate.path) {
                return candidate
            }
        }
        return nil
    }

    public func environment(for executable: URL) async -> [String: String] {
        var environment = ProcessInfo.processInfo.environment
        var path = [executable.deletingLastPathComponent().path]
        path += await shellPath()
        path += wellKnownDirectories()
        path += (environment["PATH"] ?? "").split(separator: ":").map(String.init)
        path += ["/usr/bin", "/bin", "/usr/sbin", "/sbin"]
        var seen = Set<String>()
        environment["PATH"] = path.filter { !$0.isEmpty && seen.insert($0).inserted }.joined(separator: ":")
        return environment
    }

    private func wellKnownDirectories() -> [String] {
        let home = homeDirectory.path
        var directories = [
            "\(home)/.local/bin",
            "\(home)/.claude/local",
            "/opt/homebrew/bin",
            "/usr/local/bin",
            "\(home)/.npm-global/bin",
            "\(home)/.bun/bin",
            "\(home)/.volta/bin",
            "\(home)/Library/pnpm",
            "\(home)/.local/share/pnpm",
        ]
        // nvm installs one node per version; prefer the newest.
        let nvm = "\(home)/.nvm/versions/node"
        if let versions = try? FileManager.default.contentsOfDirectory(atPath: nvm) {
            directories += versions
                .sorted { $0.compare($1, options: .numeric) == .orderedDescending }
                .map { "\(nvm)/\($0)/bin" }
        }
        return directories
    }

    private func shellPath() async -> [String] {
        if let loginShellPath { return loginShellPath }
        let shell = ProcessInfo.processInfo.environment["SHELL"].flatMap { $0.isEmpty ? nil : $0 } ?? "/bin/zsh"
        // Interactive + login so both ~/.zprofile (Homebrew) and ~/.zshrc (nvm) apply. The
        // markers fence PATH off from anything the rc files print.
        let result = await runner.run(
            URL(fileURLWithPath: shell),
            arguments: ["-ilc", "printf '__AGENTGAUGE_PATH__%s__AGENTGAUGE_END__' \"$PATH\""],
            environment: ProcessInfo.processInfo.environment,
            timeout: 5,
            captureOutput: true)
        var directories: [String] = []
        if let data = result.output, let text = String(data: data, encoding: .utf8),
           let start = text.range(of: "__AGENTGAUGE_PATH__"),
           let end = text.range(of: "__AGENTGAUGE_END__", range: start.upperBound..<text.endIndex) {
            directories = text[start.upperBound..<end.lowerBound].split(separator: ":").map(String.init)
        }
        loginShellPath = directories
        return directories
    }
}

public protocol DelegatedRefreshing: Sendable {
    /// Makes the official CLI refresh its own OAuth token. True when the CLI actually ran
    /// (the caller should re-read the credential); false when skipped.
    func tryRefresh() async -> Bool
}

/// Delegated OAuth refresh: AgentGauge never refreshes a token itself. It runs a CLI command
/// whose bootstrap refreshes an expired access token and rewrites the CLI's own credential
/// (file or keychain item), then the provider re-reads it. The refresh happens early in the
/// bootstrap, so even a slow or timed-out run leaves the fresh token behind. Each attempt
/// arms a cooldown so a genuinely signed-out user does not spawn the CLI on every poll.
public actor DelegatedTokenRefresher: DelegatedRefreshing {
    static let successCooldown: TimeInterval = 300
    static let retryCooldown: TimeInterval = 60

    private let command: CLICommand
    private let timeout: TimeInterval
    private let locator: any CLILocating
    private let runner: any ProcessRunning
    private let clock: any MonotonicClock
    private var cooldownUntil: TimeInterval?

    public init(command: CLICommand, timeout: TimeInterval, locator: any CLILocating,
                runner: any ProcessRunning, clock: any MonotonicClock = SystemMonotonicClock()) {
        self.command = command
        self.timeout = timeout
        self.locator = locator
        self.runner = runner
        self.clock = clock
    }

    public func tryRefresh() async -> Bool {
        let now = clock.now()
        if let cooldownUntil, now < cooldownUntil { return false }
        // Armed before the first await so a reentrant call cannot start a second CLI.
        cooldownUntil = now + timeout + Self.retryCooldown

        guard let executable = await locator.locate(command.executable) else {
            cooldownUntil = clock.now() + Self.successCooldown
            return false
        }
        let environment = await locator.environment(for: executable)
        let result = await runner.run(executable, arguments: command.arguments, environment: environment,
                                      timeout: timeout, captureOutput: false)
        guard result.launched else {
            cooldownUntil = clock.now() + Self.retryCooldown
            Log.write("auth", "\(command.executable) delegated refresh failed to launch")
            return false
        }
        cooldownUntil = clock.now() + (result.timedOut ? Self.retryCooldown : Self.successCooldown)
        Log.write("auth", "\(command.displayString) delegated refresh ran (exit=\(result.exitCode.map { String($0) } ?? "-"), timedOut=\(result.timedOut))")
        return true
    }
}
