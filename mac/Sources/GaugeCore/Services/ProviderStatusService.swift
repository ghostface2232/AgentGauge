import Foundation

public enum ProviderStatusLevel: String, Sendable {
    case unknown
    case operational
    case minor
    case major
    case critical
    case maintenance
}

/// Public service health, independent of account credentials and usage snapshots: it never
/// changes usage caches, auth state, notifications or usage colours.
public struct ProviderStatus: Sendable, Equatable {
    public var tool: ToolKind
    public var level: ProviderStatusLevel
    public var summary: String
    public var checkedAt: Date?
    public var lastCheckFailed: Bool

    public init(tool: ToolKind, level: ProviderStatusLevel, summary: String, checkedAt: Date?, lastCheckFailed: Bool = false) {
        self.tool = tool
        self.level = level
        self.summary = summary
        self.checkedAt = checkedAt
        self.lastCheckFailed = lastCheckFailed
    }

    /// Operational is the quiet default; anything else — an incident, or a check that could not
    /// confirm health — is worth a chip. Unknown never reads as healthy.
    public var isVisible: Bool { lastCheckFailed || level != .operational }

    public var label: String {
        if lastCheckFailed { return L10n.get("ServiceStatus_Stale") }
        switch level {
        case .minor: return L10n.get("ServiceStatus_Minor")
        case .major, .critical: return L10n.get("ServiceStatus_Major")
        case .maintenance: return L10n.get("ServiceStatus_Maintenance")
        case .unknown, .operational: return L10n.get("ServiceStatus_Unknown")
        }
    }

    public var detail: String {
        let checked = checkedAt.map {
            $0.formatted(Date.FormatStyle(date: .numeric, time: .shortened).locale(L10n.locale))
        } ?? "–"
        return L10n.format("ServiceStatus_Detail", summary.isEmpty ? L10n.get("ServiceStatus_Unknown") : summary, checked)
    }
}

/// Unauthenticated polling of each shown tool's public Statuspage (`/api/v2/status.json`)
/// every 15 minutes on its own session, so no credential-bearing header can ever reach it. A
/// failed check keeps the last observation, flagged unconfirmed with its original time.
public actor ProviderStatusService {
    public typealias Listener = @Sendable ([ToolKind: ProviderStatus]) -> Void

    static let pollInterval: TimeInterval = 15 * 60
    static let maximumSummaryLength = 300

    private let transport: any HTTPTransport
    private let clock: any MonotonicClock
    private let wallClock: @Sendable () -> Date
    private var tools: [ToolKind] = []
    private var statuses: [ToolKind: ProviderStatus] = [:]
    private var attempts: [ToolKind: TimeInterval] = [:]
    private var listener: Listener?
    private var loop: Task<Void, Never>?
    private var polling = false

    public init(transport: any HTTPTransport = URLSessionTransport(), clock: any MonotonicClock = SystemMonotonicClock(),
                wallClock: @escaping @Sendable () -> Date = { Date() }) {
        self.transport = transport
        self.clock = clock
        self.wallClock = wallClock
    }

    public func setListener(_ listener: @escaping Listener) {
        self.listener = listener
    }

    /// The shown tools. A tool that is hidden drops its status at once; one shown is polled
    /// on the next pass.
    public func setTools(_ tools: [ToolKind]) async {
        self.tools = tools.filter { $0.statusPageURL != nil }
        for tool in statuses.keys where !self.tools.contains(tool) {
            statuses[tool] = nil
            attempts[tool] = nil
        }
        listener?(statuses)
        await poll()
    }

    public func start() {
        guard loop == nil else { return }
        loop = Task {
            while !Task.isCancelled {
                await self.poll()
                try? await Task.sleep(for: .seconds(60))
            }
        }
    }

    public func poll() async {
        guard !polling else { return }
        polling = true
        defer { polling = false }
        let now = clock.now()
        let due = tools.filter { tool in attempts[tool].map { now - $0 >= Self.pollInterval } ?? true }
        guard !due.isEmpty else { return }
        for tool in due { attempts[tool] = now }

        let previous = statuses
        let results = await withTaskGroup(of: ProviderStatus.self) { group in
            for tool in due {
                group.addTask { await self.fetch(tool, previous: previous[tool]) }
            }
            var collected: [ProviderStatus] = []
            for await status in group { collected.append(status) }
            return collected
        }
        for status in results where tools.contains(status.tool) {
            statuses[status.tool] = status
        }
        listener?(statuses)
    }

    private func fetch(_ tool: ToolKind, previous: ProviderStatus?) async -> ProviderStatus {
        guard let page = tool.statusPageURL else {
            return ProviderStatus(tool: tool, level: .unknown, summary: "", checkedAt: nil, lastCheckFailed: true)
        }
        do {
            var request = URLRequest(url: page.appending(path: "api/v2/status.json"))
            request.timeoutInterval = 15
            request.setValue("application/json", forHTTPHeaderField: "Accept")
            let (data, response) = try await transport.send(request)
            try HTTPStatus.ensureSuccess(response, now: wallClock())
            return try Self.parse(tool, data: data, checkedAt: wallClock())
        } catch {
            // Public response text is deliberately left out of diagnostics.
            Log.write("status", "\(tool.displayName) public status unavailable: \(type(of: error))")
            if var previous {
                previous.lastCheckFailed = true
                return previous
            }
            return ProviderStatus(tool: tool, level: .unknown, summary: "", checkedAt: nil, lastCheckFailed: true)
        }
    }

    /// An unrecognised indicator is a parse failure, never "healthy".
    static func parse(_ tool: ToolKind, data: Data, checkedAt: Date) throws -> ProviderStatus {
        guard let status = try JSONObject.parse(data).object("status") else { throw UsageFetchError.invalidResponse }
        let level: ProviderStatusLevel
        switch status.string("indicator") {
        case "none": level = .operational
        case "minor": level = .minor
        case "major": level = .major
        case "critical": level = .critical
        case "maintenance": level = .maintenance
        default: throw UsageFetchError.invalidResponse
        }
        // Untrusted public text: bounded and single-line before it reaches any UI surface.
        var summary = (status.string("description") ?? "")
            .split(whereSeparator: \.isWhitespace).joined(separator: " ")
        if summary.count > maximumSummaryLength { summary = String(summary.prefix(maximumSummaryLength)) }
        return ProviderStatus(tool: tool, level: level, summary: summary, checkedAt: checkedAt)
    }
}
