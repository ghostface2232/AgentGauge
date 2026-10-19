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
    /// The last good snapshot; kept through failures and shown with its capture time.
    public var snapshot: UsageSnapshot?
    public var lastRefreshFailed = false
    public var auth: AuthState = .unknown
    public var isRefreshing = false
    /// When the current read started, so the UI can cap how long its indicator spins. Set
    /// only when a read starts: cache re-emits and repeated requests never extend it.
    public var refreshStartedAt: Date?

    public var id: ToolKind { tool }

    public init(tool: ToolKind, snapshot: UsageSnapshot? = nil) {
        self.tool = tool
        self.snapshot = snapshot
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

    var isDebounced: Bool { self == .popoverOpened || self == .systemResumed }
}

/// Schedules and isolates provider reads. A one-minute tick refreshes only providers whose
/// own cadence is due, stretched by `AdaptiveRefreshPolicy` while nobody looks; opening the
/// popover forces a read of every shown provider, debounced to one per 10 seconds and held
/// back by each tool's cost floor (`ToolKind.forcedRefreshFloor`); a failure keeps the last
/// good snapshot and never blocks another provider.
public actor UsageCoordinator {
    public typealias Listener = @Sendable ([ToolState]) -> Void

    static let debounce: TimeInterval = 10
    static let tick: Duration = .seconds(60)

    private let providers: [ToolKind: any UsageProvider]
    private let cache: UsageCacheStore?
    private let clock: any MonotonicClock
    private var listener: Listener?
    private var enabled: [ToolKind] = []
    private var states: [ToolKind: ToolState] = [:]
    private var lastAttempt: [ToolKind: TimeInterval] = [:]
    private var inFlight = Set<ToolKind>()
    private var lastDebouncedRefresh: TimeInterval?
    private var lastPanelOpened: TimeInterval?
    private var signals = AdaptiveRefreshSignals()
    private var lastDecision: AdaptiveRefreshDecision?
    private var scheduler: Task<Void, Never>?
    private let wallClock: @Sendable () -> Date

    public init(providers: [any UsageProvider], cache: UsageCacheStore?, clock: any MonotonicClock = SystemMonotonicClock(),
                wallClock: @escaping @Sendable () -> Date = { Date() }) {
        self.wallClock = wallClock
        var byTool: [ToolKind: any UsageProvider] = [:]
        for provider in providers { byTool[provider.tool] = provider }
        self.providers = byTool
        self.cache = cache
        self.clock = clock
        for snapshot in cache?.load() ?? [] where byTool[snapshot.tool] != nil {
            states[snapshot.tool] = ToolState(tool: snapshot.tool, snapshot: snapshot)
        }
    }

    public func setListener(_ listener: @escaping Listener) {
        self.listener = listener
        emit()
    }

    /// The shown tools in display order. Hidden tools keep their cached state but are not
    /// polled or emitted.
    public func setEnabledTools(_ tools: [ToolKind]) {
        enabled = tools.filter { providers[$0] != nil }
        emit()
    }

    /// Low Power Mode or a locked session stretch the periodic cadence.
    public func setSignals(_ signals: AdaptiveRefreshSignals) {
        self.signals = signals
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

    public func refresh(_ reason: RefreshReason) async {
        let now = clock.now()
        // Even a debounced open counts as interaction: someone is looking.
        if reason == .popoverOpened { lastPanelOpened = now }
        if reason.isDebounced {
            if let last = lastDebouncedRefresh, now - last < Self.debounce {
                emit()
                return
            }
            lastDebouncedRefresh = now
        }

        let decision = reason == .scheduled ? adaptiveDecision(now: now) : nil
        let due = enabled.filter { tool in
            guard !inFlight.contains(tool) else { return false }
            guard let last = lastAttempt[tool] else { return true }
            switch reason {
            case .scheduled:
                return now - last >= (decision?.interval(for: tool.refreshInterval) ?? tool.refreshInterval)
            case .popoverOpened:
                return now - last >= tool.forcedRefreshFloor
            default:
                return true
            }
        }
        guard !due.isEmpty else {
            emit()
            return
        }

        for tool in due {
            inFlight.insert(tool)
            lastAttempt[tool] = now
            states[tool, default: ToolState(tool: tool)].isRefreshing = true
            states[tool, default: ToolState(tool: tool)].refreshStartedAt = wallClock()
        }
        emit()

        let interaction = reason.interaction
        await withTaskGroup(of: (ToolKind, Result<ProviderOutcome, any Error>).self) { group in
            for tool in due {
                guard let provider = providers[tool] else { continue }
                group.addTask {
                    do {
                        return (tool, .success(try await provider.fetch(interaction)))
                    } catch {
                        return (tool, .failure(error))
                    }
                }
            }
            for await (tool, result) in group {
                apply(result, to: tool)
                inFlight.remove(tool)
                emit()
            }
        }
    }

    private func apply(_ result: Result<ProviderOutcome, any Error>, to tool: ToolKind) {
        var state = states[tool] ?? ToolState(tool: tool)
        state.isRefreshing = false
        state.refreshStartedAt = nil
        switch result {
        case .success(let outcome):
            state.lastRefreshFailed = false
            state.snapshot = outcome.snapshot
            if !outcome.signedIn {
                state.auth = .signedOut
            } else if outcome.isLive && !outcome.snapshot.windows.isEmpty {
                // Only a genuine live read proves the server accepted the token, so only it
                // clears a sticky rejection; a cache serve after a 401 must not.
                state.auth = .signedIn
            } else if state.auth != .rejected {
                state.auth = .signedIn
            }
            states[tool] = state
            if outcome.isLive || !outcome.signedIn { persistCache() }
        case .failure(let error):
            if HTTPUsageProvider.isCancellation(error) {
                states[tool] = state
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
                Log.write("coordinator", "\(tool.displayName) refresh failed: \(Self.describe(error))")
            }
            states[tool] = state
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
        let current = enabled.map { states[$0] ?? ToolState(tool: $0) }
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
            let ordered = snapshots.sorted { $0.tool.rawValue < $1.tool.rawValue }
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
