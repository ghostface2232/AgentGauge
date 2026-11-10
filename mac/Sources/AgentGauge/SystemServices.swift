import AppKit
import Darwin
import GaugeCore
import UserNotifications

/// Posts usage alerts through Notification Center, which owns queuing, history, Focus and
/// Do Not Disturb. Alerts carry no actions; a click simply dismisses.
@MainActor
final class NotificationService: NSObject, UNUserNotificationCenterDelegate {
    private let center: UNUserNotificationCenter?

    override init() {
        if Bundle.main.bundleIdentifier != nil {
            let center = UNUserNotificationCenter.current()
            self.center = center
            super.init()
            center.delegate = self
        } else {
            self.center = nil
            super.init()
        }
    }

    func authorizationStatus() async -> UNAuthorizationStatus {
        guard let center else { return .notDetermined }
        return await center.notificationSettings().authorizationStatus
    }

    func requestAuthorization() async -> UNAuthorizationStatus {
        guard let center else { return .notDetermined }
        do {
            _ = try await center.requestAuthorization(options: [.alert, .sound])
        } catch {
            let nsError = error as NSError
            Log.write("notify", "Notification authorization failed: \(nsError.domain) \(nsError.code)")
        }
        return await authorizationStatus()
    }

    func post(_ notification: UsageNotification) {
        guard let center else { return }
        let content = UNMutableNotificationContent()
        content.title = notification.title
        content.body = notification.message
        // Only the danger crossing earns a sound; everything else arrives quietly.
        if notification.level == .danger { content.sound = .default }
        content.threadIdentifier = notification.accountKey.storageKey
        let request = UNNotificationRequest(identifier: UUID().uuidString, content: content, trigger: nil)
        center.add(request) { error in
            if let error { Log.write("notify", "Notification not shown: \(type(of: error))") }
        }
    }

    /// A plain informational banner (the post-update confirmation), quiet and threadless.
    func postInformational(title: String, body: String) {
        guard let center else { return }
        let content = UNMutableNotificationContent()
        content.title = title
        content.body = body
        center.add(UNNotificationRequest(identifier: UUID().uuidString, content: content, trigger: nil)) { error in
            if let error { Log.write("notify", "Notification not shown: \(type(of: error))") }
        }
    }

    // A menu bar app is often frontmost while its panel is open; show the banner anyway.
    nonisolated func userNotificationCenter(
        _ center: UNUserNotificationCenter, willPresent notification: UNNotification
    ) async -> UNNotificationPresentationOptions {
        [.banner, .list, .sound]
    }
}

/// Sign-in is delegated to the official CLI, run where the user can see and answer it:
/// a `.command` file opened in Terminal. AgentGauge never handles the login itself.
/// Opens an app that owns its own sign-in (Cursor), by bundle id with the usual install
/// location as a fallback.
@MainActor
enum AppLauncher {
    static func open(bundleIdentifier: String, name: String) {
        let workspace = NSWorkspace.shared
        let fallback = URL(fileURLWithPath: "/Applications/\(name).app", isDirectory: true)
        guard let url = workspace.urlForApplication(withBundleIdentifier: bundleIdentifier)
            ?? (FileManager.default.fileExists(atPath: fallback.path) ? fallback : nil) else {
            Log.write("auth", "\(name) is not installed; cannot open it for sign-in")
            return
        }
        workspace.openApplication(at: url, configuration: NSWorkspace.OpenConfiguration())
    }
}

enum TerminalLauncher {
    static func run(_ command: CLICommand) async throws {
        let locator = CLILocator()
        // An absolute path survives shells whose rc files don't put the CLI on PATH.
        let executable = await locator.locate(command.executable)?.path ?? command.executable
        let line = ([executable] + command.arguments).map(shellQuoted).joined(separator: " ")
        let directory = FileManager.default.temporaryDirectory.appending(path: "AgentGauge", directoryHint: .isDirectory)
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        let script = directory.appending(path: "\(command.executable)-login.command")
        try "#!/bin/zsh -il\nclear\n\(line)\n".write(to: script, atomically: true, encoding: .utf8)
        try FileManager.default.setAttributes([.posixPermissions: 0o700], ofItemAtPath: script.path)
        await MainActor.run { _ = NSWorkspace.shared.open(script) }
    }

    /// Waits for the CLI's exit status only. The visible Terminal owns all login output.
    static func runAndWait(_ login: CLICommand, profile: AccountCLIProfile? = nil) async throws {
        let locator = CLILocator()
        guard let executable = await locator.locate(login.executable) else { throw LoginError.launchFailed }
        let fileManager = FileManager.default
        if let profile {
            try fileManager.createDirectory(at: profile.directory, withIntermediateDirectories: true,
                                            attributes: [.posixPermissions: 0o700])
        }
        let directory = fileManager.temporaryDirectory.appending(path: "AgentGauge/login-\(UUID().uuidString)",
                                                                  directoryHint: .isDirectory)
        try fileManager.createDirectory(at: directory, withIntermediateDirectories: true,
                                        attributes: [.posixPermissions: 0o700])
        defer { try? fileManager.removeItem(at: directory) }
        let script = directory.appending(path: "sign-in.command")
        let result = directory.appending(path: "result")
        let environment = await locator.environment(for: executable)
        var variables = profile?.variables ?? [:]
        variables["PATH"] = environment["PATH"] ?? "/usr/bin:/bin"
        if profile == nil { variables["CODEX_HOME"] = environment["CODEX_HOME"] }
        var clearedVariables = profile?.clearedVariables ?? []
        if profile == nil && variables["CODEX_HOME"] == nil { clearedVariables.append("CODEX_HOME") }
        var arguments = ["/usr/bin/env"]
        arguments += clearedVariables.flatMap { ["-u", $0] }
        arguments += variables.sorted(by: { $0.key < $1.key }).map { "\($0.key)=\($0.value)" }
        arguments.append(executable.path)
        arguments += login.arguments
        let command = arguments.map(shellQuoted).joined(separator: " ")
        let home = shellQuoted(fileManager.homeDirectoryForCurrentUser.path)
        let marker = shellQuoted(result.path)
        let pidFile = directory.appending(path: "pid")
        let pidMarker = shellQuoted(pidFile.path)
        let cancel = shellQuoted("printf failed > \(marker); exit 130")
        let contents = """
            #!/bin/zsh -f
            trap \(cancel) HUP INT TERM
            printf '%s' "$$" > \(pidMarker)
            if cd -- \(home) && \(command); then
                printf ok > \(marker)
            else
                printf failed > \(marker)
            fi
            """
        try (contents + "\n").write(to: script, atomically: true, encoding: .utf8)
        try fileManager.setAttributes([.posixPermissions: 0o700], ofItemAtPath: script.path)
        let opened = await MainActor.run { NSWorkspace.shared.open(script) }
        guard opened else { throw LoginError.launchFailed }
        let clock = ContinuousClock()
        let startupDeadline = clock.now.advanced(by: .seconds(30))
        let deadline = clock.now.advanced(by: .seconds(600))
        while clock.now < deadline {
            switch try? String(contentsOf: result, encoding: .utf8) {
            case "ok": return
            case "failed": throw LoginError.unsuccessful
            default:
                if let rawPID = try? String(contentsOf: pidFile, encoding: .utf8),
                   let pid = Int32(rawPID), pid > 1, kill(pid, 0) == -1, errno == ESRCH {
                    // The shell may have written the result between our first read and kill.
                    if (try? String(contentsOf: result, encoding: .utf8)) == "ok" { return }
                    throw LoginError.unsuccessful
                }
                if clock.now >= startupDeadline && !fileManager.fileExists(atPath: pidFile.path) {
                    throw LoginError.launchFailed
                }
                try await Task.sleep(for: .seconds(1))
            }
        }
        throw LoginError.unsuccessful
    }

    private enum LoginError: Error { case launchFailed, unsuccessful }

    private static func shellQuoted(_ value: String) -> String {
        "'" + value.replacingOccurrences(of: "'", with: "'\\''") + "'"
    }
}
