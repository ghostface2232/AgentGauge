import Darwin
import Foundation

/// A running Antigravity language server Gauge can attach to. The token is a secret, so the
/// description leaves it out.
struct AntigravityLanguageServer: Sendable, CustomStringConvertible {
    let pid: pid_t
    let csrfToken: String
    let ports: [Int]

    var description: String { "AntigravityLanguageServer(pid=\(pid), ports=\(ports))" }
}

/// One process as discovery sees it; a seam so matching is testable without real processes.
struct AntigravityProcessCandidate: Sendable {
    let pid: pid_t
    let executablePath: String
    let arguments: [String]
}

/// Finds the language servers the running Antigravity app started: every same-user process
/// whose executable name starts with `language_server`, lies inside the Antigravity bundle and
/// carries a `--csrf_token`. The (privacy-sensitive) argv is read only for processes whose
/// executable already passed the name check, and the listening ports come from the kernel's
/// socket table for that PID.
///
/// PIDs are reused, so the process's start time is read before its argv and again after its
/// ports: a PID that changed hands mid-scan is dropped instead of being sent the token.
/// Discovery never throws — a process that exits mid-scan or one we may not inspect simply
/// yields no candidate, and the attach reader falls back to delegate mode.
struct AntigravityProcessDiscovery: Sendable {
    let install: AntigravityInstall?

    func discover() -> [AntigravityLanguageServer] {
        let ownUID = getuid()
        var servers: [AntigravityLanguageServer] = []
        for pid in DarwinProcessTable.allPIDs() where pid != getpid() {
            guard let path = DarwinProcessTable.executablePath(pid),
                  AntigravityProcessMatch.isLanguageServerName(path),
                  DarwinProcessTable.uid(pid) == ownUID,
                  let started = DarwinProcessTable.startTime(pid),
                  let argv = DarwinProcessTable.arguments(pid) else { continue }
            let candidate = AntigravityProcessCandidate(pid: pid, executablePath: path, arguments: argv.arguments)
            guard let token = Self.token(for: candidate, install: install) else { continue }
            let ports = DarwinProcessTable.loopbackListeningPorts(pid)
            guard DarwinProcessTable.startTime(pid) == started else { continue }
            servers.append(AntigravityLanguageServer(pid: pid, csrfToken: token, ports: ports))
        }
        return servers
    }

    /// The CSRF token of a candidate that passes `AntigravityProcessMatch`, else nil.
    /// argv[0] is skipped: it is the process's own name for itself, not a flag.
    static func token(for candidate: AntigravityProcessCandidate, install: AntigravityInstall?) -> String? {
        let commandLine = AntigravityCommandLine(Array(candidate.arguments.dropFirst()))
        guard AntigravityProcessMatch.isCandidate(
            executablePath: candidate.executablePath, install: install, commandLine: commandLine) else { return nil }
        return ProviderText.normalize(commandLine.value(of: "--csrf_token"))
    }
}
