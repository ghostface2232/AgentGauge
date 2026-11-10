import Foundation

public enum AuthState: Sendable, Equatable {
    case unknown
    case signedIn
    case signedOut
    /// The server rejected the token (sticky until a genuine live read succeeds).
    case rejected
    case keychainDenied
    case unreadable
}

public struct ToolState: Sendable, Equatable, Identifiable {
    public var tool: ToolKind
    public let account: ToolAccount?
    /// The last good snapshot; kept through failures and shown with its capture time.
    public var snapshot: UsageSnapshot?
    public var lastRefreshFailed = false
    public var auth: AuthState = .unknown
    public var isRefreshing = false
    /// When the current read started, in `SystemMonotonicClock` seconds, so the UI can cap how
    /// long its indicator spins without a wall-clock change stretching or cutting it. Set only
    /// when a read starts: cache re-emits and repeated requests never extend it.
    public var refreshStartedAt: TimeInterval?
    /// True until this run has read the tool successfully: `snapshot` came from the disk cache.
    public var isRehydrated = false

    public var id: UsageAccountKey { account?.key ?? UsageAccountKey(tool: tool) }
    public var displayName: String { account?.displayName ?? tool.displayName }

    public init(tool: ToolKind, snapshot: UsageSnapshot? = nil, account: ToolAccount? = nil) {
        self.tool = tool
        self.snapshot = snapshot
        self.account = account
    }
}

public enum RefreshReason: Sendable {
    case launch
    case scheduled
    case popoverOpened
    case manual
    case systemResumed
    case toolsChanged

    /// Explicit user actions pierce a rate-limit cooldown; a wake or a popover open does not.
    var interaction: FetchInteraction {
        switch self {
        case .manual, .toolsChanged: .user
        default: .background
        }
    }

    /// The requests that can pile up: repeated clicks, or a wake-then-unlock pair. Explicit
    /// clicks are in here too, because they pierce a rate-limit cooldown; without the debounce
    /// each click would be another real attempt against a throttling endpoint. A tool change
    /// is not, so its card appears or disappears right away.
    var isDebounced: Bool { self == .popoverOpened || self == .manual || self == .systemResumed }
}

/// Schedules and isolates provider reads. A one-minute tick refreshes only providers whose
/// own cadence is due, stretched by `AdaptiveRefreshPolicy` while nobody looks; opening the
/// popover or the refresh button forces a read of every shown provider, debounced to one per
/// 10 seconds (the popover open also held back by each tool's cost floor,
/// `ToolKind.forcedRefreshFloor`); a failure keeps the last good snapshot and never blocks
/// another provider, and a read that hangs is given up on at `fetchDeadline`.
public actor UsageCoordinator {
    public typealias Listener = @Sendable ([ToolState]) -> Void

    static let debounce: TimeInterval = 10
    static let tick: Duration = .seconds(60)
    /// Past the sum of what a healthy read can take: the keychain tool's 60 s, a delegated
    /// refresh (up to 30 s, plus the login-shell probe) and the HTTP read's 30 s.
    public static let defaultFetchDeadline: Duration = .seconds(150)

    private var providers: [UsageAccountKey: any UsageProvider]
    private var accounts: [ToolAccount]
    private let cache: UsageCacheStore?
    private let clock: any MonotonicClock
    private let fetchDeadline: Duration
    private var listener: Listener?
    private var enabled: [ToolKind] = []
    private var states: [UsageAccountKey: ToolState] = [:]
    private var lastAttempt: [UsageAccountKey: TimeInterval] = [:]
    /// Tools whose read has not actually ended, which can outlast its deadline.
    private var inFlight = Set<UsageAccountKey>()
    /// A sign-in may finish while this account is still returning its earlier reading.
    private var pendingAccountRefreshes = Set<UsageAccountKey>()
    private var signingIn = Set<UsageAccountKey>()
    /// The `refresh` calls still waiting on a tool's read: resumed at its end or its deadline.
    private var readWaiters: [UsageAccountKey: CheckedContinuation<Void, Never>] = [:]
    /// When a refresh last started fetching anything, whatever asked for it. A refresh that
    /// fetched nothing does not count, so it cannot hold back one that would.
    private var lastFetchStarted: TimeInterval?
    private var lastPanelOpened: TimeInterval?
    private var signals = AdaptiveRefreshSignals()
    private var lastDecision: AdaptiveRefreshDecision?
    private var scheduler: Task<Void, Never>?
    private let wallClock: @Sendable () -> Date

    public init(providers: [any UsageProvider], cache: UsageCacheStore?, clock: any MonotonicClock = SystemMonotonicClock(),
                wallClock: @escaping @Sendable () -> Date = { Date() },
                fetchDeadline: Duration = UsageCoordinator.defaultFetchDeadline,
                additionalProviders: [AccountUsageProvider] = []) {
        self.wallClock = wallClock
        self.fetchDeadline = fetchDeadline
        var byAccount: [UsageAccountKey: any UsageProvider] = [:]
        for provider in providers { byAccount[UsageAccountKey(tool: provider.tool)] = provider }
        for provider in additionalProviders { byAccount[provider.account.key] = provider }
        self.providers = byAccount
        self.accounts = additionalProviders.map(\.account)
        self.cache = cache
        self.clock = clock
        let profiles = additionalProviders.map(\.account)
        for snapshot in cache?.load() ?? [] where byAccount[snapshot.accountKey] != nil {
            if snapshot.tool == .githubCopilot, snapshot.profileID == nil,
               profiles.contains(where: { $0.tool == .githubCopilot }) { continue }
            let account = profiles.first { $0.key == snapshot.accountKey }
            var state = ToolState(tool: snapshot.tool, snapshot: snapshot, account: account)
            state.isRehydrated = true
            states[snapshot.accountKey] = state
        }
    }

    public func setListener(_ listener: @escaping Listener) {
        self.listener = listener
        emit()
    }

    /// The shown tools in display order. Hidden tools keep their cached state but are not
    /// polled or emitted.
    public func setEnabledTools(_ tools: [ToolKind]) {
        enabled = tools.filter { providers[UsageAccountKey(tool: $0)] != nil }
        emit()
    }

    public func addAccount(_ provider: AccountUsageProvider) {
        let account = provider.account
        guard providers[account.key] == nil else { return }
        providers[account.key] = provider
        accounts.append(account)
        if account.tool == .githubCopilot {
            let fallback = UsageAccountKey(tool: .githubCopilot)
            states.removeValue(forKey: fallback)
            lastAttempt.removeValue(forKey: fallback)
            pendingAccountRefreshes.remove(fallback)
            persistCache()
        }
        emit()
    }

    public func removeAccount(_ key: UsageAccountKey) {
        guard key.profileID != nil else { return }
        accounts.removeAll { $0.key == key }
        providers.removeValue(forKey: key)
        states.removeValue(forKey: key)
        lastAttempt.removeValue(forKey: key)
        pendingAccountRefreshes.remove(key)
        readWaiters.removeValue(forKey: key)?.resume()
        persistCache()
        emit()
    }

    private var enabledAccounts: [UsageAccountKey] {
        enabled.flatMap { tool in
            [UsageAccountKey(tool: tool)] + accounts.filter { $0.tool == tool }.map(\.key)
        }.filter(tracks)
    }

    private func tracks(_ key: UsageAccountKey) -> Bool {
        providers[key] != nil && (key.tool != .githubCopilot || key.profileID != nil
                                  || !accounts.contains { $0.tool == .githubCopilot })
    }

    private func emptyState(_ key: UsageAccountKey) -> ToolState {
        ToolState(tool: key.tool, account: accounts.first { $0.key == key })
    }

    /// Low Power Mode or a locked session stretch the periodic cadence.
    public func setSignals(_ signals: AdaptiveRefreshSignals) {
        self.signals = signals
    }

    public func setSigningIn(_ active: Bool, account: UsageAccountKey) {
        if active { signingIn.insert(account) }
        else { signingIn.remove(account) }
    }

    public func start() {
        guard scheduler == nil else { return }
        scheduler = Task {
            await self.refresh(.launch)
            while !Task.isCancelled {
                try? await Task.sleep(for: Self.tick)
                guard !Task.isCancelled else { break }
                await self.refresh(.scheduled)
            }
        }
    }

    public func refresh(_ reason: RefreshReason, account: UsageAccountKey? = nil) async {
        if reason == .toolsChanged, let account, enabledAccounts.contains(account), inFlight.contains(account) {
            pendingAccountRefreshes.insert(account)
            return
        }
        let now = clock.now()
        // Even a debounced open counts as interaction: someone is looking.
        if reason == .popoverOpened { lastPanelOpened = now }
        if reason.isDebounced, let last = lastFetchStarted, now - last < Self.debounce {
            emit()
            return
        }

        let decision = reason == .scheduled ? adaptiveDecision(now: now) : nil
        let due = enabledAccounts.filter { key in
            guard (account == nil || key == account), !inFlight.contains(key), !signingIn.contains(key) else { return false }
            guard let last = lastAttempt[key] else { return true }
            switch reason {
            case .scheduled:
                return now - last >= (decision?.interval(for: key.tool.refreshInterval) ?? key.tool.refreshInterval)
            case .popoverOpened:
                return now - last >= key.tool.forcedRefreshFloor
            default:
                return true
            }
        }
        guard !due.isEmpty else {
            emit()
            return
        }

        lastFetchStarted = now
        for key in due {
            inFlight.insert(key)
            lastAttempt[key] = now
            states[key, default: emptyState(key)].isRefreshing = true
            states[key, default: emptyState(key)].refreshStartedAt = now
        }
        emit()

        let interaction = reason.interaction
        await withTaskGroup(of: Void.self) { group in
            for key in due {
                guard let provider = providers[key] else { continue }
                group.addTask { await self.read(key, from: provider, interaction: interaction) }
            }
        }
    }

    /// One provider read, waited on until it ends or `fetchDeadline` passes. The read runs in
    /// its own unstructured task because a task group cannot leave a child behind: a read that
    /// ignores cancellation would hold the group, and with it the scheduler and every other tool.
    private func read(_ key: UsageAccountKey, from provider: any UsageProvider, interaction: FetchInteraction) async {
        guard tracks(key) else {
            inFlight.remove(key)
            return
        }
        await withCheckedContinuation { (waiter: CheckedContinuation<Void, Never>) in
            readWaiters[key] = waiter
            let deadline = fetchDeadline
            let work = Task.detached { () async -> Result<ProviderOutcome, any Error> in
                do {
                    return .success(try await provider.fetch(interaction))
                } catch {
                    return .failure(error)
                }
            }
            let timer = Task.detached {
                try? await Task.sleep(for: deadline)
                guard !Task.isCancelled else { return }
                work.cancel()
                await self.deadlinePassed(key)
            }
            Task.detached {
                let result = await work.value
                timer.cancel()
                await self.readEnded(key, result)
            }
        }
    }

    /// The read counts as failed from here on, but the tool stays in flight until it really
    /// ends: nothing else may start beside it, because providers rely on never running two
    /// fetches of one tool at once and a hung read must not pile up more of itself.
    private func deadlinePassed(_ key: UsageAccountKey) {
        guard let waiter = readWaiters.removeValue(forKey: key) else { return }
        if tracks(key) {
            apply(.failure(UsageFetchError.timedOut), to: key)
            emit()
        }
        waiter.resume()
    }

    /// Also takes a read that ended past its deadline. Its result is still real: a success
    /// replaces the failure shown meanwhile, and the expected cancellation changes nothing.
    private func readEnded(_ key: UsageAccountKey, _ result: Result<ProviderOutcome, any Error>) {
        inFlight.remove(key)
        if tracks(key) {
            apply(result, to: key)
            emit()
        }
        readWaiters.removeValue(forKey: key)?.resume()
        if pendingAccountRefreshes.remove(key) != nil {
            Task { await refresh(.toolsChanged, account: key) }
        }
    }

    private func apply(_ result: Result<ProviderOutcome, any Error>, to key: UsageAccountKey) {
        var state = states[key] ?? emptyState(key)
        state.isRefreshing = false
        state.refreshStartedAt = nil
        switch result {
        case .success(let outcome):
            state.lastRefreshFailed = false
            state.isRehydrated = false
            state.snapshot = outcome.snapshot
            state.snapshot?.profileID = key.profileID
            if !outcome.signedIn {
                state.auth = .signedOut
            } else if outcome.isLive && !outcome.snapshot.windows.isEmpty {
                // Only a genuine live read proves the server accepted the token, so only it
                // clears a sticky rejection; a cache serve after a 401 must not.
                state.auth = .signedIn
            } else if state.auth != .rejected {
                state.auth = .signedIn
            }
            states[key] = state
            if outcome.isLive || !outcome.signedIn { persistCache() }
        case .failure(let error):
            if HTTPUsageProvider.isCancellation(error) {
                states[key] = state
                return
            }
            state.lastRefreshFailed = true
            switch error as? UsageFetchError {
            case .authenticationRequired:
                state.auth = .rejected
            case .credentialUnreadable(.keychainDenied):
                state.auth = .keychainDenied
            case .credentialUnreadable:
                state.auth = .unreadable
            default:
                Log.write("coordinator", "\(key.tool.displayName) refresh failed: \(Self.describe(error))")
            }
            states[key] = state
        }
    }

    private func adaptiveDecision(now: TimeInterval) -> AdaptiveRefreshDecision {
        let decision = AdaptiveRefreshPolicy.evaluate(sincePanelOpened: lastPanelOpened.map { now - $0 }, signals: signals)
        if decision != lastDecision {
            // Only policy transitions are logged, never account or activity detail.
            Log.write("refresh", "reason=\(decision.reason.rawValue) multiplier=\(decision.multiplier) cap=30m")
            lastDecision = decision
        }
        return decision
    }

    private func persistCache() {
        // Only snapshots with windows are worth rehydrating; a signed-out tool drops out.
        let snapshots = states.values.compactMap { state -> UsageSnapshot? in
            guard state.auth != .signedOut, let snapshot = state.snapshot, !snapshot.windows.isEmpty else { return nil }
            return snapshot
        }
        cache?.save(snapshots)
    }

    private func emit() {
        let current = enabledAccounts.map { states[$0] ?? emptyState($0) }
        listener?(current)
    }

    private static func describe(_ error: any Error) -> String {
        if let fetch = error as? UsageFetchError {
            switch fetch {
            case .http(let status, _): return "HTTP \(status)"
            case .cooldown: return "rate-limit cooldown"
            case .invalidResponse: return "invalid response"
            default: return "\(fetch)"
            }
        }
        if let url = error as? URLError { return "URLError \(url.code.rawValue)" }
        return String(describing: type(of: error))
    }
}

/// The last-known snapshots, so a relaunch shows values (with their capture time) before the
/// first read completes. Never holds credentials.
public struct UsageCacheStore: Sendable {
    let url: URL

    public init(directory: URL) {
        url = directory.appending(path: "usage-cache.json")
    }

    public static func defaultDirectory() -> URL {
        let base = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask).first
            ?? FileManager.default.homeDirectoryForCurrentUser.appending(path: "Library/Application Support")
        return base.appending(path: "AgentGauge", directoryHint: .isDirectory)
    }

    public func load() -> [UsageSnapshot] {
        guard let data = try? Data(contentsOf: url) else { return [] }
        let decoder = JSONDecoder()
        decoder.dateDecodingStrategy = .iso8601
        do {
            return try decoder.decode(CacheDocument.self, from: data).snapshots
        } catch {
            Log.write("cache", "Usage cache unreadable, ignoring: \(type(of: error))")
            return []
        }
    }

    public func save(_ snapshots: [UsageSnapshot]) {
        let encoder = JSONEncoder()
        encoder.dateEncodingStrategy = .iso8601
        encoder.outputFormatting = [.sortedKeys]
        do {
            try FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
            let ordered = snapshots.sorted { $0.accountKey.storageKey < $1.accountKey.storageKey }
            try encoder.encode(CacheDocument(snapshots: ordered)).write(to: url, options: .atomic)
        } catch {
            Log.write("cache", "Usage cache write failed: \(type(of: error))")
        }
    }

    private struct CacheDocument: Codable {
        var version = 1
        var snapshots: [UsageSnapshot]
    }
}
