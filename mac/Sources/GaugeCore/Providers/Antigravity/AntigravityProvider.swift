import Foundation

/// Antigravity is installed but no engine could be reached or it returned no usable usage
/// (signed out, still warming, a transport error, or an availability-only response). A
/// provider failure, so the coordinator keeps the last good snapshot instead of clearing the
/// card — distinct from "not installed", which is a clean empty result.
public struct AntigravityUnavailableError: Error, Sendable, Equatable, CustomStringConvertible {
    public let reason: String
    public var description: String { "Antigravity unavailable: \(reason)" }
}

/// A way to read usage from an Antigravity engine. A nil result means "this source could not
/// reach an engine — try the next".
protocol AntigravityReader: Sendable {
    func reading() async throws -> AntigravityReading?
}

/// Reads usage from Antigravity's local language server. There is no public usage endpoint
/// and no Gauge-readable credential: the data comes from the engine the app runs (attach) or,
/// with the app closed, from an engine Gauge launches itself (delegate), which authenticates
/// with the login on disk. Process discovery, the loopback Connect transport, the delegate
/// engine's lifecycle and parsing all stay behind this provider; callers see only a
/// normalized `UsageSnapshot`.
///
/// Failure handling follows the coordinator's contract: not installed is a clean empty
/// snapshot; every other unusable outcome throws so the last good snapshot is preserved.
public struct AntigravityProvider: UsageProvider {
    public let tool = ToolKind.antigravity

    private let readers: [any AntigravityReader]
    private let isInstalled: @Sendable () -> Bool
    private let now: @Sendable () -> Date

    /// The production wiring. The install is located per fetch, so installing or removing the
    /// app while AgentGauge runs is picked up at the next refresh.
    public init() {
        self.init(searchRoots: AntigravityInstall.defaultSearchRoots())
    }

    init(searchRoots: [URL]) {
        let client = AntigravityLoopbackClient()
        let locate: @Sendable () -> AntigravityInstall? = { AntigravityInstall.locate(in: searchRoots) }
        readers = [
            AntigravityAttachReader(locate: locate, client: client),
            AntigravityDelegateReader(locate: locate, client: client),
        ]
        isInstalled = { locate()?.hasEngine == true }
        now = { Date() }
    }

    init(readers: [any AntigravityReader], isInstalled: @escaping @Sendable () -> Bool, now: @escaping @Sendable () -> Date = { Date() }) {
        self.readers = readers
        self.isInstalled = isInstalled
        self.now = now
    }

    /// Antigravity has no rate-limit gate, so who asked does not matter; the coordinator's
    /// cadence and popover floor are what bound delegate launches.
    public func fetch(_ interaction: FetchInteraction) async throws -> ProviderOutcome {
        guard isInstalled() else {
            return ProviderOutcome(
                snapshot: UsageSnapshot(tool: tool, plan: nil, windows: [], capturedAt: now()),
                signedIn: false, isLive: false)
        }

        var reading: AntigravityReading?
        for reader in readers {
            reading = try await reader.reading()
            if reading != nil { break }
        }
        guard let reading else {
            throw AntigravityUnavailableError(reason: "no engine returned usage")
        }

        let windows = try AntigravityQuotaParser.windows(from: reading.quotaJSON)
        // An availability-only response with no known windows must not overwrite a useful cache.
        guard !windows.isEmpty else {
            throw AntigravityUnavailableError(reason: "no usage windows")
        }
        return ProviderOutcome(
            snapshot: UsageSnapshot(tool: tool, plan: reading.plan, windows: windows, capturedAt: now()),
            signedIn: true, isLive: true)
    }
}

/// Reads usage from a running Antigravity app by attaching to its language server(s). Several
/// may run, but they share one account, so each is queried and the richest response — the
/// one parsing into the most windows — is kept, guarding against one still warming up.
struct AntigravityAttachReader: AntigravityReader {
    let locate: @Sendable () -> AntigravityInstall?
    let client: AntigravityLoopbackClient

    func reading() async throws -> AntigravityReading? {
        var readings: [AntigravityReading] = []
        for server in AntigravityProcessDiscovery(install: locate()).discover() where !server.ports.isEmpty {
            if let reading = try await client.reading(ports: server.ports, csrfToken: server.csrfToken) {
                readings.append(reading)
            }
        }
        return Self.richest(readings)
    }

    static func richest(_ readings: [AntigravityReading]) -> AntigravityReading? {
        var best: AntigravityReading?
        var bestCount = -1
        for reading in readings {
            let count = (try? AntigravityQuotaParser.windows(from: reading.quotaJSON).count) ?? 0
            if count > bestCount {
                best = reading
                bestCount = count
            }
        }
        return best
    }
}

/// Reads usage by launching the engine itself (delegate mode) when no app engine answered.
/// The host is created once so its gate serializes every delegate read of this provider.
struct AntigravityDelegateReader: AntigravityReader {
    private let locate: @Sendable () -> AntigravityInstall?
    private let client: AntigravityLoopbackClient
    private let gate = AntigravitySerialGate()

    init(locate: @escaping @Sendable () -> AntigravityInstall?, client: AntigravityLoopbackClient) {
        self.locate = locate
        self.client = client
    }

    func reading() async throws -> AntigravityReading? {
        await gate.acquire()
        defer { gate.release() }
        return try await AntigravityEngineHost(install: locate(), client: client).reading()
    }
}
