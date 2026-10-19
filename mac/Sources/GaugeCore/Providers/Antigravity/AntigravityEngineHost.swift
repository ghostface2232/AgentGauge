import Darwin
import Foundation

/// Runs the Antigravity language server itself when the app is closed (delegate mode), so
/// usage refreshes without the app open. The engine authenticates with the login already on
/// disk, exactly as when the app starts it; Gauge never reads or writes those credentials.
///
/// Each read spawns a fresh engine, takes one reading and tears it down again: usage only
/// changes while the app is in use (attach mode), so a resident language server between
/// refreshes would be cost for no benefit. `AntigravityDelegateReader` serializes reads so two
/// refreshes never spawn competing engines.
///
/// macOS has no Job Objects, so the engine's tree is bounded three ways, none of which can
/// reach a process Gauge did not start:
/// - `POSIX_SPAWN_SETPGROUP` makes the engine leader of a fresh process group, signalled with
///   `killpg`. The leader is reaped only after the last group signal, so its PID — and with
///   it the group id — cannot be reused by an unrelated process while Gauge still signals it.
/// - Its stdin is a pipe only Gauge holds, with `--exit_on_stdin_close`: closing it is the
///   graceful stop, and if AgentGauge crashes or is force-quit mid-read the kernel closes it
///   and the engine exits on its own.
/// - A per-spawn random environment marker is inherited by every descendant; after the group
///   is gone, same-user processes started since the spawn that still carry that exact marker
///   (a sidecar that left the group with `setsid`) are killed. The marker and start time are
///   re-read immediately before each signal. This sweep is best-effort: recent macOS
///   releases (verified on 27) no longer expose another process's environment, so there it
///   finds nothing and the first two bounds are what hold.
/// Residual risk: a descendant that both leaves the process group and scrubs its environment
/// cannot be identified, and on a hard kill of AgentGauge a sidecar that ignores its parent's
/// exit outlives it. The engine started no sidecars during a quota read when verified live.
///
/// Returns nil — never throws, apart from cancellation — for ordinary conditions: not
/// installed, signed out, or an engine that failed to become ready.
struct AntigravityEngineHost: Sendable {
    static let readyTimeout: Duration = .seconds(15)
    static let pollInterval: Duration = .milliseconds(250)
    static let terminationGrace: TimeInterval = 2
    static let markerVariable = "AGENTGAUGE_ANTIGRAVITY_ENGINE"

    private let install: AntigravityInstall?
    private let client: AntigravityLoopbackClient

    init(install: AntigravityInstall?, client: AntigravityLoopbackClient) {
        self.install = install
        self.client = client
    }

    func reading() async throws -> AntigravityReading? {
        guard let install, install.hasEngine else { return nil }
        try Task.checkCancellation()

        let token = UUID().uuidString
        let engine: SpawnedEngine
        do {
            engine = try SpawnedEngine.spawn(
                executable: install.engineExecutable.path,
                arguments: AntigravityEngineLaunch.arguments(csrfToken: token, ideVersion: install.ideVersion),
                marker: (Self.markerVariable, UUID().uuidString))
        } catch {
            Log.write("provider", "Antigravity engine spawn failed: \(error)")
            return nil
        }

        do {
            let result = try await waitForReading(engine, token: token)
            await engine.terminate()
            return result
        } catch {
            await engine.terminate()
            throw error
        }
    }

    private func waitForReading(_ engine: SpawnedEngine, token: String) async throws -> AntigravityReading? {
        let clock = ContinuousClock()
        let deadline = clock.now + Self.readyTimeout
        while clock.now < deadline {
            try Task.checkCancellation()
            if let status = engine.exitStatus() {
                Log.write("provider", "Antigravity engine exited during startup (\(status))")
                return nil
            }
            // Readiness is a real quota response, not an open port: the server binds its ports
            // seconds before it answers (about 7s on first launch when verified live).
            let ports = DarwinProcessTable.loopbackListeningPorts(engine.pid)
            if !ports.isEmpty, let reading = try await client.reading(ports: ports, csrfToken: token) {
                return reading
            }
            try await Task.sleep(for: Self.pollInterval)
        }
        Log.write("provider", "Antigravity engine gave no usage within \(Self.readyTimeout)")
        return nil
    }
}

/// A language server Gauge started, and everything needed to stop it and its descendants.
struct SpawnedEngine: Sendable {
    struct SpawnError: Error, CustomStringConvertible {
        let operation: String
        let code: Int32
        var description: String { "\(operation) failed: errno \(code)" }
    }

    let pid: pid_t
    let stdinWriter: Int32
    let marker: (name: String, value: String)
    let spawnedAt: UInt64

    static func spawn(executable: String, arguments: [String], marker: (String, String)) throws -> SpawnedEngine {
        var pipeEnds: [Int32] = [0, 0]
        guard pipe(&pipeEnds) == 0 else { throw SpawnError(operation: "pipe", code: errno) }
        let (reader, writer) = (pipeEnds[0], pipeEnds[1])
        // Keep the writer out of every other child the app starts; were it inherited, the
        // engine would not see EOF when Gauge closes its own copy.
        _ = fcntl(reader, F_SETFD, FD_CLOEXEC)
        _ = fcntl(writer, F_SETFD, FD_CLOEXEC)
        defer { close(reader) }

        var actions: posix_spawn_file_actions_t?
        posix_spawn_file_actions_init(&actions)
        defer { posix_spawn_file_actions_destroy(&actions) }
        posix_spawn_file_actions_adddup2(&actions, reader, STDIN_FILENO)
        // The engine's output carries account details and sign-in URLs: discard it unread.
        posix_spawn_file_actions_addopen(&actions, STDOUT_FILENO, "/dev/null", O_WRONLY, 0)
        posix_spawn_file_actions_addopen(&actions, STDERR_FILENO, "/dev/null", O_WRONLY, 0)
        posix_spawn_file_actions_addchdir_np(&actions, (executable as NSString).deletingLastPathComponent)

        var attributes: posix_spawnattr_t?
        posix_spawnattr_init(&attributes)
        defer { posix_spawnattr_destroy(&attributes) }
        // A fresh process group led by the engine; only stdio crosses over (CLOEXEC_DEFAULT);
        // signal dispositions and mask reset, so an ignored SIGPIPE/SIGTERM is not inherited.
        posix_spawnattr_setflags(&attributes, Int16(POSIX_SPAWN_SETPGROUP | POSIX_SPAWN_CLOEXEC_DEFAULT
            | POSIX_SPAWN_SETSIGDEF | POSIX_SPAWN_SETSIGMASK))
        posix_spawnattr_setpgroup(&attributes, 0)
        var allSignals = sigset_t()
        sigfillset(&allSignals)
        posix_spawnattr_setsigdefault(&attributes, &allSignals)
        var noSignals = sigset_t()
        sigemptyset(&noSignals)
        posix_spawnattr_setsigmask(&attributes, &noSignals)

        var environment = ProcessInfo.processInfo.environment
        environment[marker.0] = marker.1
        let argv = ([executable] + arguments).map { strdup($0) } + [nil]
        let envp = environment.map { strdup("\($0.key)=\($0.value)") } + [nil]
        defer {
            argv.forEach { free($0) }
            envp.forEach { free($0) }
        }

        let started = Self.spawnFloorMicroseconds()
        var pid: pid_t = 0
        let result = posix_spawn(&pid, executable, &actions, &attributes, argv, envp)
        guard result == 0 else {
            close(writer)
            throw SpawnError(operation: "posix_spawn", code: result)
        }
        return SpawnedEngine(pid: pid, stdinWriter: writer, marker: marker, spawnedAt: started)
    }

    /// The exit status if the engine has exited, without reaping it: the zombie keeps the PID
    /// (and so the process group id) reserved until `terminate` has sent its last signal.
    func exitStatus() -> Int32? {
        var info = siginfo_t()
        guard waitid(P_PID, id_t(pid), &info, WEXITED | WNOHANG | WNOWAIT) == 0, info.si_pid == pid else { return nil }
        return info.si_status
    }

    /// Stops the engine and every descendant Gauge can attribute to it. Runs off the
    /// cooperative pool (it waits out a short grace period) and is not cancellable, so a
    /// cancelled read still tears its engine down.
    func terminate() async {
        await withCheckedContinuation { (continuation: CheckedContinuation<Void, Never>) in
            DispatchQueue.global(qos: .utility).async {
                terminateNow()
                continuation.resume()
            }
        }
    }

    private func terminateNow() {
        // Graceful first: the engine exits on stdin EOF (about half a second, verified live).
        close(stdinWriter)
        killpg(pid, SIGTERM)
        let graceDeadline = Date().addingTimeInterval(AntigravityEngineHost.terminationGrace)
        while exitStatus() == nil, Date() < graceDeadline { usleep(50_000) }

        // Anything still in the group (the engine or a sidecar ignoring SIGTERM) is killed while
        // the unreaped leader still pins the group id, then the leader is reaped.
        killpg(pid, SIGKILL)
        let reapDeadline = Date().addingTimeInterval(AntigravityEngineHost.terminationGrace)
        var status: Int32 = 0
        while waitpid(pid, &status, WNOHANG) == 0, Date() < reapDeadline { usleep(20_000) }

        killMarkedDescendants()
    }

    /// Sidecars that left the process group still inherit the marker. Only same-user processes
    /// started since the spawn are inspected, and each is re-confirmed right before the signal.
    private func killMarkedDescendants() {
        let uid = getuid()
        let entry = "\(marker.name)=\(marker.value)"
        func isOurs(_ candidate: pid_t) -> UInt64? {
            guard candidate != getpid(), DarwinProcessTable.uid(candidate) == uid,
                  let started = DarwinProcessTable.startTime(candidate), started >= spawnedAt,
                  DarwinProcessTable.arguments(candidate)?.environment?.contains(entry) == true else { return nil }
            return started
        }
        for candidate in DarwinProcessTable.allPIDs() {
            guard let started = isOurs(candidate) else { continue }
            if DarwinProcessTable.startTime(candidate) == started {
                kill(candidate, SIGKILL)
                Log.write("provider", "Antigravity engine sidecar outlived its group; killed")
            }
        }
    }

    /// Now, less a second of slack, as the earliest start time a descendant can have.
    private static func spawnFloorMicroseconds() -> UInt64 {
        var now = timeval()
        gettimeofday(&now, nil)
        return UInt64(now.tv_sec) * 1_000_000 + UInt64(now.tv_usec) - 1_000_000
    }
}

/// A FIFO async mutex: serializes delegate reads so two refreshes never run two engines.
final class AntigravitySerialGate: @unchecked Sendable {
    private let lock = NSLock()
    private var busy = false
    private var waiters: [CheckedContinuation<Void, Never>] = []

    func acquire() async {
        await withCheckedContinuation { (continuation: CheckedContinuation<Void, Never>) in
            lock.lock()
            if busy {
                waiters.append(continuation)
                lock.unlock()
            } else {
                busy = true
                lock.unlock()
                continuation.resume()
            }
        }
    }

    func release() {
        lock.lock()
        if waiters.isEmpty {
            busy = false
            lock.unlock()
        } else {
            let next = waiters.removeFirst()
            lock.unlock()
            next.resume()
        }
    }
}
