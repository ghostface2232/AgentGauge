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
    /// How long a timed-out process gets to honour SIGTERM before SIGKILL.
    static let terminationGrace: TimeInterval = 2
    /// How long stdout may stay open after the process itself has exited. What the process
    /// wrote is already in the pipe, so this only waits out a descendant's last words.
    static let drainGrace: TimeInterval = 1

    public init() {}

    public func run(_ executable: URL, arguments: [String], environment: [String: String],
                    timeout: TimeInterval, captureOutput: Bool) async -> ProcessResult {
        await withCheckedContinuation { continuation in
            let process = Process()
            process.executableURL = executable
            process.arguments = arguments
            process.environment = environment
            // The user's home rather than the inherited folder, so a CLI never picks up project
            // files (or a planted binary) from wherever AgentGauge was started.
            process.currentDirectoryURL = FileManager.default.homeDirectoryForCurrentUser
            process.standardInput = FileHandle.nullDevice
            process.standardError = FileHandle.nullDevice
            let pipe: Pipe? = captureOutput ? Pipe() : nil
            process.standardOutput = pipe ?? FileHandle.nullDevice
            let state = RunState()
            // Drained while the process runs: output larger than the pipe buffer (the keychain
            // document grows with every MCP OAuth entry) would otherwise block the writer and
            // only end at the timeout. Started before the launch, so a process that exits at
            // once cannot be judged before its output has anywhere to go.
            let drain = pipe.map { OutputDrain($0.fileHandleForReading) }

            let deliver: @Sendable (Int32?, Data?) -> Void = { exitCode, output in
                state.finishOnce {
                    continuation.resume(returning: ProcessResult(
                        exitCode: exitCode, timedOut: state.timedOut, launched: true, output: output))
                }
            }
            process.terminationHandler = { finished in
                let status = finished.terminationStatus
                guard let drain else { return deliver(status, nil) }
                drain.whenEnded { deliver(status, $0) }
                // Exit does not end the pipe while a descendant still holds its write end (a
                // daemon started by ~/.zshrc during the login-shell probe), so it is bounded.
                drain.stop(after: Self.drainGrace)
            }
            do {
                try process.run()
            } catch {
                drain?.stop()
                state.finishOnce { continuation.resume(returning: .launchFailed) }
                return
            }
            DispatchQueue.global().asyncAfter(deadline: .now() + timeout) {
                guard process.isRunning else { return }
                state.markTimedOut()
                process.terminate()
                DispatchQueue.global().asyncAfter(deadline: .now() + Self.terminationGrace) {
                    if process.isRunning { kill(process.processIdentifier, SIGKILL) }
                    // The termination handler normally finishes first. This holds the timeout
                    // even for a process that never comes back from SIGKILL (stuck in the
                    // kernel), so the caller is resumed no matter what.
                    DispatchQueue.global().asyncAfter(deadline: .now() + 2 * Self.drainGrace) {
                        guard let drain else { return deliver(nil, nil) }
                        drain.whenEnded { deliver(nil, $0) }
                        drain.stop()
                    }
                }
            }
        }
    }

    /// Collects a child's stdout through a dispatch source rather than a blocking read, because
    /// the read side must be able to give up on a pipe that never reaches EOF. Closing a
    /// FileHandle under a thread blocked in `readDataToEndOfFile` is undefined, and that call
    /// reports a failed read by raising an Objective-C exception, which would crash the app;
    /// cancelling a source is the supported way to stop, and its cancel handler is the one
    /// safe place to close the descriptor. A descendant that writes after that gets EPIPE.
    private final class OutputDrain: @unchecked Sendable {
        private static let chunkSize = 64 * 1024

        private let descriptor: Int32
        private let queue = DispatchQueue(label: "io.github.ghostface2232.agentgauge.process-output")
        private let source: DispatchSourceRead
        // Confined to `queue`.
        private var collected = Data()
        private var ended = false
        private var waiters: [@Sendable (Data) -> Void] = []

        init(_ handle: FileHandle) {
            descriptor = handle.fileDescriptor
            // A spurious wake-up must come back as EAGAIN instead of parking the serial queue.
            // The flag belongs to the read end's own file description; the child's stdout is
            // the write end and stays blocking.
            _ = fcntl(descriptor, F_SETFL, fcntl(descriptor, F_GETFL) | O_NONBLOCK)
            source = DispatchSource.makeReadSource(fileDescriptor: descriptor, queue: queue)
            source.setEventHandler { [self] in readAvailable() }
            source.setCancelHandler { try? handle.close() }
            source.resume()
        }

        /// Calls back with everything read once the pipe reaches EOF or `stop` gives up on it.
        func whenEnded(_ waiter: @escaping @Sendable (Data) -> Void) {
            queue.async { [self] in
                if ended {
                    waiter(collected)
                } else {
                    waiters.append(waiter)
                }
            }
        }

        /// Forces the end of the output: takes what is already buffered and closes the read end.
        func stop(after delay: TimeInterval = 0) {
            queue.asyncAfter(deadline: .now() + delay) { [self] in
                readAvailable()
                end()
            }
        }

        private func readAvailable() {
            let capacity = Self.chunkSize
            var buffer = [UInt8](repeating: 0, count: capacity)
            while !ended {
                let count = Darwin.read(descriptor, &buffer, capacity)
                if count > 0 {
                    collected.append(contentsOf: buffer[..<count])
                } else if count < 0 && errno == EINTR {
                    continue
                } else if count < 0 && errno == EAGAIN {
                    return
                } else {
                    return end()
                }
            }
        }

        private func end() {
            guard !ended else { return }
            ended = true
            source.cancel()
            let current = waiters
            waiters = []
            current.forEach { $0(collected) }
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
/// shell PATH (where nvm/asdf/Homebrew put things) is the fallback. A good answer is kept for
/// the process lifetime; a failed probe (a slow ~/.zshrc, say) is asked again after a cooldown.
///
/// Last comes the copy a desktop app ships, so a desktop-only install can still sign in and
/// run the delegated refresh: the Claude desktop app unpacks the real `claude` binary under
/// its Application Support folder, and the Codex app bundles `codex` and shares
/// `~/.codex/auth.json` with the CLI. The desktop apps' own token stores are never read.
public actor CLILocator: CLILocating {
    /// A failed probe costs up to its 5-second timeout, and every delegated refresh or `gh`
    /// lookup asks for this PATH, so failures are not retried on each of those.
    static let shellProbeRetryCooldown: TimeInterval = 300

    private let runner: any ProcessRunning
    private let homeDirectory: URL
    private let clock: any MonotonicClock
    private var loginShellPath: [String]?
    private var shellProbe: Task<[String]?, Never>?
    private var shellProbeRetryAt: TimeInterval?

    public init(runner: any ProcessRunning = SystemProcessRunner(),
                homeDirectory: URL = FileManager.default.homeDirectoryForCurrentUser,
                clock: any MonotonicClock = SystemMonotonicClock()) {
        self.runner = runner
        self.homeDirectory = homeDirectory
        self.clock = clock
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
    /// The app writes a `.verified` marker into the build folder after the binary, so a build
    /// still downloading is passed over for an older complete one; an unmarked binary is used
    /// only when no build carries the marker.
    static func newestClaudeBinary(in root: URL) -> URL? {
        let fileManager = FileManager.default
        guard let versions = try? fileManager.contentsOfDirectory(atPath: root.path) else { return nil }
        let ordered = versions
            .filter { $0.first?.isNumber == true }
            .sorted { $0.compare($1, options: .numeric) == .orderedDescending }
        var unverified: URL?
        for version in ordered {
            let directory = root.appending(path: version)
            var folders = [directory]
            if let builds = try? fileManager.contentsOfDirectory(atPath: directory.path) {
                folders += builds.sorted().map { directory.appending(path: $0) }
            }
            for folder in folders {
                let candidates = [folder.appending(path: "claude.app/Contents/MacOS").path, folder.path]
                guard let binary = firstExecutable("claude", in: candidates) else { continue }
                if fileManager.fileExists(atPath: folder.appending(path: ".verified").path) { return binary }
                if unverified == nil { unverified = binary }
            }
        }
        return unverified
    }

    /// PATH entries that name one fixed folder. A relative entry (`.`, `bin`, an empty one)
    /// resolves against the process's working directory, so a binary planted in whatever
    /// folder AgentGauge started from could be located, or found by `env` when a CLI starts
    /// `node`, and run with the user's rights.
    static func absoluteDirectories(_ directories: [String]) -> [String] {
        directories.filter { $0.hasPrefix("/") }
    }

    private static func firstExecutable(_ name: String, in directories: [String]) -> URL? {
        for directory in absoluteDirectories(directories) {
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
        environment["PATH"] = Self.absoluteDirectories(path).filter { seen.insert($0).inserted }.joined(separator: ":")
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
        // The locator is shared by every provider, so concurrent callers share one shell.
        if let shellProbe { return await shellProbe.value ?? [] }
        if let shellProbeRetryAt, clock.now() < shellProbeRetryAt { return [] }

        let runner = runner
        let probe = Task { await Self.probeLoginShell(runner: runner) }
        shellProbe = probe
        let directories = await probe.value
        shellProbe = nil
        if let directories {
            loginShellPath = directories
            shellProbeRetryAt = nil
        } else {
            shellProbeRetryAt = clock.now() + Self.shellProbeRetryCooldown
        }
        return directories ?? []
    }

    private static func probeLoginShell(runner: any ProcessRunning) async -> [String]? {
        let shell = ProcessInfo.processInfo.environment["SHELL"].flatMap { $0.isEmpty ? nil : $0 } ?? "/bin/zsh"
        // Interactive + login so both ~/.zprofile (Homebrew) and ~/.zshrc (nvm) apply. The
        // markers fence PATH off from anything the rc files print.
        let result = await runner.run(
            URL(fileURLWithPath: shell),
            arguments: ["-ilc", "printf '__AGENTGAUGE_PATH__%s__AGENTGAUGE_END__' \"$PATH\""],
            environment: ProcessInfo.processInfo.environment,
            timeout: 5,
            captureOutput: true)
        // Judged by the answer, not the exit: a shell that printed PATH and then hung at exit
        // still gave a good one. The output can carry account details, so it is never logged.
        if let directories = parseLoginShellPath(result.output) { return directories }
        Log.write("auth", "Login shell PATH probe failed (launched=\(result.launched), exit=\(result.exitCode.map { String($0) } ?? "-"), timedOut=\(result.timedOut))")
        return nil
    }

    /// The directories between the probe's markers; nil when they are absent or fence nothing.
    /// Decoded leniently, so a stray non-UTF-8 byte from an rc file cannot cost the answer.
    static func parseLoginShellPath(_ output: Data?) -> [String]? {
        guard let output else { return nil }
        let text = String(decoding: output, as: UTF8.self)
        guard let start = text.range(of: "__AGENTGAUGE_PATH__"),
              let end = text.range(of: "__AGENTGAUGE_END__", range: start.upperBound..<text.endIndex) else { return nil }
        let directories = text[start.upperBound..<end.lowerBound].split(separator: ":").map(String.init)
        return directories.isEmpty ? nil : directories
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
