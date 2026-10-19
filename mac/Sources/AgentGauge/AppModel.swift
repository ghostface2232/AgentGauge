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

    var viewMode: UsageViewMode {
        didSet { defaults.set(viewMode.rawValue, forKey: Keys.viewMode) }
    }

    /// Hiding keeps the recorded history, so switching it back on needs no refresh.
    var showSparkline: Bool {
        didSet { defaults.set(showSparkline, forKey: Keys.showSparkline) }
    }

    var paceModel: WeeklyPaceModel {
        didSet { defaults.set(paceModel.rawValue, forKey: Keys.paceModel) }
    }

    /// Off by default: turning it on is what grants the local session-log reads.
    var showApiCost: Bool {
        didSet {
            defaults.set(showApiCost, forKey: Keys.showApiCost)
            apiCostToggled()
        }
    }

    /// Public service health per shown tool (only the polled ones appear).
    private(set) var serviceStatuses: [ToolKind: ProviderStatus] = [:]
    /// This month's API-equivalent estimate per tool, while the option is on.
    private(set) var apiCostEstimates: [ToolKind: ApiCostEstimate] = [:]

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

    /// The card refresh indicator stops after this long even if a read is still out, so a
    /// hung provider never spins forever; the last-updated time still tells the truth.
    static let refreshIndicatorCeiling: TimeInterval = 30

    private let defaults = UserDefaults.standard
    private let coordinator: UsageCoordinator
    private let statusService = ProviderStatusService()
    private let history = UsageHistoryStore()
    private let monotonic = SystemMonotonicClock()
    /// False until the 28-day warm-up has run, so no main-thread read waits on it.
    private var historyReady = false
    /// Bumped when recorded readings land, so rows re-derive their trends.
    private var historyRevision = 0
    /// Set by `PreviewRenderer`: draws a mode without persisting it.
    var viewModeOverride: UsageViewMode?
    /// The `--render-preview` instance records no history and posts no alerts.
    let isPreview = PreviewRenderer.directoryArgument() != nil
    private let apiCost = ApiCostService()
    /// Bumped whenever the cost option turns off, so a result already on its way to the main
    /// actor from an older run can never bring the chips back.
    @ObservationIgnored private var apiCostGeneration = 0
    private let notifier = NotificationService()
    @ObservationIgnored private var evaluator = UsageNotificationEvaluator()
    @ObservationIgnored private var observers: [NSObjectProtocol] = []
    @ObservationIgnored private var sessionLocked = false

    init() {
        defaults.register(defaults: [
            Keys.showPercent: true,
            Keys.notifyThresholds: true,
            Keys.notifyResets: true,
            Keys.showSparkline: true,
            Keys.showApiCost: false,
        ])
        showPercentInMenuBar = defaults.bool(forKey: Keys.showPercent)
        displayBasis = DisplayBasis(rawValue: defaults.string(forKey: Keys.displayBasis) ?? "") ?? .used
        viewMode = UsageViewMode(rawValue: defaults.string(forKey: Keys.viewMode) ?? "") ?? .bar
        showSparkline = defaults.bool(forKey: Keys.showSparkline)
        paceModel = WeeklyPaceModel(rawValue: defaults.string(forKey: Keys.paceModel) ?? "") ?? .uniform
        showApiCost = defaults.bool(forKey: Keys.showApiCost)
        notifyThresholds = defaults.bool(forKey: Keys.notifyThresholds)
        notifyResets = defaults.bool(forKey: Keys.notifyResets)
        hiddenTools = Self.loadHiddenTools(defaults)
        coordinator = UsageCoordinator(
            providers: ProviderFactory.makeDefault(),
            cache: UsageCacheStore(directory: UsageCacheStore.defaultDirectory()))

        // The 28-day weekday fold is paid here, off the main thread, not by the first card
        // that reads history.
        let history = history
        Task { [weak self] in
            await Task.detached(priority: .utility) { history.warm() }.value
            self?.historyReady = true
        }
        Task { await start() }
    }

    private func start() async {
        await coordinator.setListener { [weak self] states in
            Task { @MainActor in self?.receive(states) }
        }
        await coordinator.setEnabledTools(visibleTools)
        await coordinator.setSignals(currentSignals())
        await coordinator.start()
        await statusService.setListener { [weak self] statuses in
            Task { @MainActor in self?.serviceStatuses = statuses }
        }
        await statusService.start()
        Task { await statusService.setTools(visibleTools) }
        startApiCost()
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
        apiCostPopoverOpened()
    }

    func refreshNow() {
        Task { await coordinator.refresh(.manual) }
    }

    func setShown(_ tool: ToolKind, _ shown: Bool) {
        if shown {
            hiddenTools.remove(tool)
            evaluator.toolShown(tool)
        } else {
            hiddenTools.insert(tool)
        }
        defaults.set(hiddenTools.map(\.rawValue).sorted(), forKey: Keys.hiddenTools)
        let visible = visibleTools
        Task {
            await coordinator.setEnabledTools(visible)
            if shown { await coordinator.refresh(.toolsChanged) }
        }
        Task { await statusService.setTools(visible) }
        apiCostToolsChanged()
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

    /// Whether the card's spinner shows: a read is out and started under the ceiling ago,
    /// measured on the monotonic clock so a wall-clock change cannot stretch or cut it.
    func isShowingRefresh(_ state: ToolState) -> Bool {
        _ = now // re-evaluated whenever `now` ticks, including the ceiling tick
        guard state.isRefreshing, let started = state.refreshStartedAt else { return false }
        return monotonic.now() - started < Self.refreshIndicatorCeiling
    }

    /// The mode the cards draw in: the user's choice, unless the preview renderer is
    /// drawing each mode in turn without persisting it.
    var effectiveViewMode: UsageViewMode { viewModeOverride ?? viewMode }

    /// Everything a row shows beside its bar, derived from the window and its recorded history.
    func details(for tool: ToolKind, window: UsageWindow) -> RowDetails {
        var details = RowDetails(reset: ResetTimeFormatter.row(window.resetTime, now: now))
        // Until the background warm-up finishes, reading would wait on its lock on the main
        // thread; the rows simply show no trend for that moment instead.
        _ = historyRevision
        let samples = historyReady ? history.recent(tool: tool, windowID: window.id) : []
        let profile = historyReady && paceModel == .automatic ? history.weekdayProfile(tool: tool, windowID: window.id) : nil
        details.pace = PaceClassifier.caption(for: window, now: now, model: paceModel, profile: profile)
        details.eta = UsageEtaClassifier.caption(samples: samples, window: window, now: now)
        if showSparkline && effectiveViewMode == .bar {
            details.burndown = UsageBurndown.build(window: window, samples: samples, now: now)
        }
        if let used = window.usedCount, let limit = window.limitCount {
            details.counts = "\(used.formatted(.number.locale(L10n.locale))) / \(limit.formatted(.number.locale(L10n.locale)))"
        }
        return details
    }

    /// The chip text, its tooltip, and the spoken label (the amount first: a label replaces
    /// the visible text for VoiceOver, so the explanation alone would never say the number).
    func apiCostText(for tool: ToolKind) -> (text: String, detail: String, accessibility: String)? {
        guard showApiCost, let estimate = apiCostEstimates[tool], let chip = ApiCostText.chip(estimate) else { return nil }
        let detail = ApiCostText.tooltip(estimate)
        return (chip, detail, ApiCostText.accessibilityLabel(estimate) ?? detail)
    }

    // MARK: API-equivalent cost

    private func startApiCost() {
        guard showApiCost else { return }
        apiCostGeneration += 1
        let generation = apiCostGeneration
        let tools = Set(visibleTools)
        let service = apiCost
        let listener: ApiCostService.Listener = { [weak self] estimates in
            Task { @MainActor in
                guard let self, self.showApiCost, self.apiCostGeneration == generation else { return }
                self.apiCostEstimates = estimates
            }
        }
        Task {
            await service.setListener(listener)
            await service.setTools(tools)
            await service.start()
        }
    }

    private func apiCostToggled() {
        if showApiCost {
            startApiCost()
        } else {
            // Cleared here, on the main actor, so every chip disappears at once; the ledger
            // stays on disk for the next time it is switched on.
            apiCostGeneration += 1
            apiCostEstimates = [:]
            Task { await apiCost.stop() }
        }
    }

    private func apiCostToolsChanged() {
        guard showApiCost else { return }
        let tools = Set(visibleTools)
        Task { await apiCost.setTools(tools) }
    }

    private func apiCostPopoverOpened() {
        guard showApiCost else { return }
        Task { await apiCost.popoverOpened() }
    }

    func announceUpdate(to version: String) {
        notifier.postInformational(title: "AgentGauge", body: L10n.format("Update_Done", version))
    }

    private func receive(_ newStates: [ToolState]) {
        states = newStates
        now = Date()
        scheduleRefreshCeilingTick(newStates)
        recordHistory(newStates)
        // The preview renderer's instance only draws; it must not alert for what it reads.
        guard !isPreview else { return }
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

    /// Only genuinely new readings reach the history: the store ignores a window whose
    /// capture time has not advanced, so cache re-serves and re-emits add nothing. The SQLite
    /// write (and the daily prune) runs off the main thread; the rows re-read once it lands.
    private func recordHistory(_ states: [ToolState]) {
        guard !isPreview else { return }
        let snapshots = states.compactMap { state in
            state.lastRefreshFailed || state.auth == .signedOut ? nil : state.snapshot
        }
        guard !snapshots.isEmpty else { return }
        let history = history
        Task { [weak self] in
            await Task.detached(priority: .utility) { snapshots.forEach(history.record) }.value
            self?.historyRevision &+= 1
        }
    }

    /// Re-renders once a running read crosses the indicator ceiling, so the spinner stops
    /// on time rather than at the next 30-second tick.
    private func scheduleRefreshCeilingTick(_ states: [ToolState]) {
        let current = monotonic.now()
        let delays = states.compactMap { $0.isRefreshing ? $0.refreshStartedAt : nil }
            .map { $0 + Self.refreshIndicatorCeiling - current }
            .filter { $0 > 0 }
        guard let delay = delays.min() else { return }
        Task {
            try? await Task.sleep(for: .seconds(delay + 0.1))
            now = Date()
        }
    }

    /// Waking from sleep or unlocking forces one refresh after a short delay: the network is
    /// often not up at the instant of wake, and a wake-then-unlock pair collapses into one
    /// read through the coordinator's debounce. Low Power Mode and a locked screen stretch
    /// the periodic cadence instead.
    private func observeSystemEvents() {
        let center = NSWorkspace.shared.notificationCenter
        for name in [NSWorkspace.didWakeNotification, NSWorkspace.sessionDidBecomeActiveNotification, NSWorkspace.screensDidWakeNotification] {
            observers.append(center.addObserver(forName: name, object: nil, queue: .main) { [weak self] _ in
                MainActor.assumeIsolated { self?.scheduleResumeRefresh() }
            })
        }
        observers.append(NotificationCenter.default.addObserver(
            forName: .NSProcessInfoPowerStateDidChange, object: nil, queue: .main
        ) { [weak self] _ in
            MainActor.assumeIsolated { self?.signalsChanged() }
        })
        // The screen lock is only announced on the distributed centre.
        let distributed = DistributedNotificationCenter.default()
        for (name, locked) in [("com.apple.screenIsLocked", true), ("com.apple.screenIsUnlocked", false)] {
            observers.append(distributed.addObserver(forName: Notification.Name(name), object: nil, queue: .main) { [weak self] _ in
                MainActor.assumeIsolated {
                    self?.sessionLocked = locked
                    self?.signalsChanged()
                }
            })
        }
    }

    private func currentSignals() -> AdaptiveRefreshSignals {
        AdaptiveRefreshSignals(lowPowerMode: ProcessInfo.processInfo.isLowPowerModeEnabled, sessionLocked: sessionLocked)
    }

    private func signalsChanged() {
        let signals = currentSignals()
        Task { await coordinator.setSignals(signals) }
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
        static let viewMode = "viewMode"
        static let showSparkline = "showSparkline"
        static let paceModel = "weeklyPaceModel"
        static let showApiCost = "showApiCost"
        static let notifyThresholds = "notifyThresholds"
        static let notifyResets = "notifyResets"
        static let hiddenTools = "hiddenTools"
        static let knownTools = "knownTools"
    }
}
