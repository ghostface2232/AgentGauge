import AppKit
import GaugeCore
import Observation
import ServiceManagement
import SwiftUI
import UserNotifications

/// The app's single source of UI state. It owns the coordinator and the system services
/// (notifications, login item, wake observation) and applies preferences; views only read
/// state and call intents. Preferences live in UserDefaults.
@MainActor
@Observable
final class AppModel {
    private(set) var states: [ToolState] = []
    /// Ticks while the app runs so relative captions ("Resets in 2h 5m") stay current.
    private(set) var now = Date()
    private(set) var notificationStatus: UNAuthorizationStatus = .notDetermined
    private(set) var loginItemStatus: SMAppService.Status = SMAppService.mainApp.status
    private(set) var settingsError: String?

    var showPercentInMenuBar: Bool {
        didSet { defaults.set(showPercentInMenuBar, forKey: Keys.showPercent) }
    }

    var displayBasis: DisplayBasis {
        didSet { defaults.set(displayBasis.rawValue, forKey: Keys.displayBasis) }
    }

    var notifyThresholds: Bool {
        didSet { notificationKindsChanged(was: oldValue || notifyResets) }
    }

    var notifyResets: Bool {
        didSet { notificationKindsChanged(was: notifyThresholds || oldValue) }
    }

    private(set) var hiddenTools: Set<ToolKind>

    var visibleTools: [ToolKind] { ToolKind.allCases.filter { !hiddenTools.contains($0) } }
    var menuBarSummary: MenuBarSummary { MenuBarSummary(states: states) }
    var isRefreshing: Bool { states.contains(where: \.isRefreshing) }

    /// The newest capture across shown tools, for the footer's "Updated HH:mm".
    var lastUpdated: Date? { states.compactMap { $0.snapshot?.capturedAt }.max() }

    private let defaults = UserDefaults.standard
    private let coordinator: UsageCoordinator
    private let notifier = NotificationService()
    @ObservationIgnored private var evaluator = UsageNotificationEvaluator()
    @ObservationIgnored private var observers: [NSObjectProtocol] = []

    init() {
        defaults.register(defaults: [
            Keys.showPercent: true,
            Keys.notifyThresholds: true,
            Keys.notifyResets: true,
        ])
        showPercentInMenuBar = defaults.bool(forKey: Keys.showPercent)
        displayBasis = DisplayBasis(rawValue: defaults.string(forKey: Keys.displayBasis) ?? "") ?? .used
        notifyThresholds = defaults.bool(forKey: Keys.notifyThresholds)
        notifyResets = defaults.bool(forKey: Keys.notifyResets)
        hiddenTools = Self.loadHiddenTools(defaults)
        coordinator = UsageCoordinator(
            providers: ProviderFactory.makeDefault(),
            cache: UsageCacheStore(directory: UsageCacheStore.defaultDirectory()))

        Task { await start() }
    }

    private func start() async {
        await coordinator.setListener { [weak self] states in
            Task { @MainActor in self?.receive(states) }
        }
        await coordinator.setEnabledTools(visibleTools)
        await coordinator.start()
        observeSystemEvents()
        notificationStatus = await notifier.authorizationStatus()
        if notifyThresholds || notifyResets, notificationStatus == .notDetermined {
            notificationStatus = await notifier.requestAuthorization()
        }
        while !Task.isCancelled {
            try? await Task.sleep(for: .seconds(30))
            now = Date()
        }
    }

    // MARK: Intents

    func popoverOpened() {
        now = Date()
        Task { await coordinator.refresh(.popoverOpened) }
    }

    func refreshNow() {
        Task { await coordinator.refresh(.manual) }
    }

    func setShown(_ tool: ToolKind, _ shown: Bool) {
        if shown { hiddenTools.remove(tool) } else { hiddenTools.insert(tool) }
        defaults.set(hiddenTools.map(\.rawValue).sorted(), forKey: Keys.hiddenTools)
        let visible = visibleTools
        Task {
            await coordinator.setEnabledTools(visible)
            if shown { await coordinator.refresh(.toolsChanged) }
        }
    }

    func signIn(_ tool: ToolKind) {
        switch tool.signIn {
        case .cli(let command):
            Task {
                do {
                    try await TerminalLauncher.run(command)
                } catch {
                    Log.write("auth", "Could not open Terminal for sign-in: \(type(of: error))")
                }
            }
        case .app(let bundleIdentifier, let name):
            AppLauncher.open(bundleIdentifier: bundleIdentifier, name: name)
        }
    }

    /// A tool that is not shown by default starts hidden the first time this build sees it,
    /// so adding a tool to a later release never pops an unwanted card onto an existing
    /// install; after that, only the user's own choice in the hidden list counts.
    private static func loadHiddenTools(_ defaults: UserDefaults) -> Set<ToolKind> {
        var hidden = Set((defaults.stringArray(forKey: Keys.hiddenTools) ?? []).compactMap(ToolKind.init(rawValue:)))
        let known = Set(defaults.stringArray(forKey: Keys.knownTools) ?? [])
        for tool in ToolKind.allCases where !tool.isShownByDefault && !known.contains(tool.rawValue) {
            hidden.insert(tool)
        }
        defaults.set(hidden.map(\.rawValue).sorted(), forKey: Keys.hiddenTools)
        defaults.set(Array(known.union(ToolKind.allCases.map(\.rawValue))).sorted(), forKey: Keys.knownTools)
        return hidden
    }

    /// Re-reads state that can change behind the app's back while settings were closed.
    func settingsAppeared() {
        loginItemStatus = SMAppService.mainApp.status
        settingsError = nil
        Task { notificationStatus = await notifier.authorizationStatus() }
    }

    var launchAtLogin: Bool {
        loginItemStatus == .enabled || loginItemStatus == .requiresApproval
    }

    func setLaunchAtLogin(_ enabled: Bool) {
        do {
            if enabled {
                try SMAppService.mainApp.register()
            } else {
                try SMAppService.mainApp.unregister()
            }
            settingsError = nil
        } catch {
            Log.write("settings", "Login item change failed: \(type(of: error))")
            settingsError = L10n.get("Settings_SaveFailed")
        }
        // Reflect the real registration, not the requested one, so a failure never lies.
        loginItemStatus = SMAppService.mainApp.status
    }

    func openNotificationSettings() {
        let id = Bundle.main.bundleIdentifier ?? ""
        if let url = URL(string: "x-apple.systempreferences:com.apple.Notifications-Settings.extension?id=\(id)") {
            NSWorkspace.shared.open(url)
        }
    }

    func openLoginItemSettings() {
        SMAppService.openSystemSettingsLoginItems()
    }

    // MARK: State

    private func receive(_ newStates: [ToolState]) {
        states = newStates
        now = Date()
        // Detection stops entirely while every kind is off; re-enabling resets the baseline.
        guard notifyThresholds || notifyResets else { return }
        let alerts = evaluator.evaluate(newStates, now: now)
        for alert in alerts where (alert.kind == .threshold && notifyThresholds) || (alert.kind == .reset && notifyResets) {
            notifier.post(alert)
        }
    }

    private func notificationKindsChanged(was wasEnabled: Bool) {
        defaults.set(notifyThresholds, forKey: Keys.notifyThresholds)
        defaults.set(notifyResets, forKey: Keys.notifyResets)
        let isEnabled = notifyThresholds || notifyResets
        if isEnabled && !wasEnabled {
            evaluator.resetBaseline()
        }
        if isEnabled && notificationStatus == .notDetermined {
            Task { notificationStatus = await notifier.requestAuthorization() }
        }
    }

    /// Waking from sleep or unlocking forces one refresh after a short delay: the network is
    /// often not up at the instant of wake, and a wake-then-unlock pair collapses into one
    /// read through the coordinator's debounce.
    private func observeSystemEvents() {
        let center = NSWorkspace.shared.notificationCenter
        for name in [NSWorkspace.didWakeNotification, NSWorkspace.sessionDidBecomeActiveNotification, NSWorkspace.screensDidWakeNotification] {
            observers.append(center.addObserver(forName: name, object: nil, queue: .main) { [weak self] _ in
                MainActor.assumeIsolated { self?.scheduleResumeRefresh() }
            })
        }
    }

    private func scheduleResumeRefresh() {
        Task {
            try? await Task.sleep(for: .seconds(5))
            await coordinator.refresh(.systemResumed)
        }
    }

    private enum Keys {
        static let showPercent = "showPercentInMenuBar"
        static let displayBasis = "displayBasis"
        static let notifyThresholds = "notifyThresholds"
        static let notifyResets = "notifyResets"
        static let hiddenTools = "hiddenTools"
        static let knownTools = "knownTools"
    }
}
