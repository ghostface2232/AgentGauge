import AppKit
import GaugeCore
import UserNotifications

/// Posts usage alerts through Notification Center, which owns queuing, history, Focus and
/// Do Not Disturb. Alerts carry no actions; a click simply dismisses.
@MainActor
final class NotificationService: NSObject, UNUserNotificationCenterDelegate {
    private let center = UNUserNotificationCenter.current()

    override init() {
        super.init()
        center.delegate = self
    }

    func authorizationStatus() async -> UNAuthorizationStatus {
        await center.notificationSettings().authorizationStatus
    }

    func requestAuthorization() async -> UNAuthorizationStatus {
        do {
            _ = try await center.requestAuthorization(options: [.alert, .sound])
        } catch {
            let nsError = error as NSError
            Log.write("notify", "Notification authorization failed: \(nsError.domain) \(nsError.code)")
        }
        return await authorizationStatus()
    }

    func post(_ notification: UsageNotification) {
        let content = UNMutableNotificationContent()
        content.title = notification.title
        content.body = notification.message
        // Only the danger crossing earns a sound; everything else arrives quietly.
        if notification.level == .danger { content.sound = .default }
        content.threadIdentifier = notification.tool.rawValue
        let request = UNNotificationRequest(identifier: UUID().uuidString, content: content, trigger: nil)
        center.add(request) { error in
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

    private static func shellQuoted(_ value: String) -> String {
        "'" + value.replacingOccurrences(of: "'", with: "'\\''") + "'"
    }
}
