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
    private let cli: (any AntigravityCLIReading)?

    /// The production wiring. The install is located per fetch, so installing or removing the
    /// app while AgentGauge runs is picked up at the next refresh.
    public init() {
        self.init(searchRoots: AntigravityInstall.defaultSearchRoots(), cli: AntigravityCLIReader(locator: CLILocator()))
    }

    init(searchRoots: [URL], cli: (any AntigravityCLIReading)?) {
        let client = AntigravityLoopbackClient()
        let locate: @Sendable () -> AntigravityInstall? = { AntigravityInstall.locate(in: searchRoots) }
        readers = [
            AntigravityAttachReader(locate: locate, client: client),
            AntigravityDelegateReader(locate: locate, client: client),
        ]
        isInstalled = { locate()?.hasEngine == true }
        now = { Date() }
        self.cli = cli
    }

    init(readers: [any AntigravityReader], isInstalled: @escaping @Sendable () -> Bool,
         now: @escaping @Sendable () -> Date = { Date() }, cli: (any AntigravityCLIReading)? = nil) {
        self.readers = readers
        self.isInstalled = isInstalled
        self.now = now
        self.cli = cli
    }

    public func fetch(_ interaction: FetchInteraction) async throws -> ProviderOutcome {
        let engineInstalled = isInstalled()
        if engineInstalled {
            for reader in readers {
                if let reading = try await reader.reading(),
                   let windows = try? AntigravityQuotaParser.windows(from: reading.quotaJSON), !windows.isEmpty {
                    return outcome(windows, plan: reading.plan)
                }
            }
        }

        // An installed engine that could not read usage is a failure even if the CLI exists.
        if (!engineInstalled || interaction == .user), let cli, let executable = await cli.locator.locate("agy") {
            guard interaction == .user else { throw UsageRefreshSkipped() }
            if let data = await cli.usage(executable),
               let windows = try? AntigravityQuotaParser.windows(from: data), !windows.isEmpty {
                return outcome(windows, plan: nil, cli: true)
            }
            try Task.checkCancellation()
            throw AntigravityUnavailableError(reason: "no CLI usage windows")
        }
        if !engineInstalled {
            return ProviderOutcome(
                snapshot: UsageSnapshot(tool: tool, plan: nil, windows: [], capturedAt: now()),
                signedIn: false, isLive: false)
        }

        throw AntigravityUnavailableError(reason: "no engine returned usage")
    }

    private func outcome(_ windows: [UsageWindow], plan: String?, cli: Bool = false) -> ProviderOutcome {
        var snapshot = UsageSnapshot(tool: tool, plan: plan, windows: windows, capturedAt: now())
        if cli {
            snapshot.historyIdentity = "antigravity-cli"
            snapshot.sourceLabel = "agy"
        }
        return ProviderOutcome(snapshot: snapshot, signedIn: true, isLive: true)
    }
}

protocol AntigravityCLIReading: Sendable {
    var locator: any CLILocating { get }
    func usage(_ executable: URL) async -> Data?
}

private struct AntigravityCLIReader: AntigravityCLIReading {
    let locator: any CLILocating
    private let runner = SystemProcessRunner()

    func usage(_ executable: URL) async -> Data? {
        let files = FileManager.default
        let directory = files.temporaryDirectory.appending(path: UUID().uuidString)
        do {
            try files.createDirectory(at: directory, withIntermediateDirectories: false,
                                      attributes: [.posixPermissions: 0o700])
        } catch { return nil }
        defer { try? files.removeItem(at: directory) }
        do {
            try files.createSymbolicLink(atPath: directory.appending(path: "open").path,
                                         withDestinationPath: "/usr/bin/false")
        } catch { return nil }

        var environment = await locator.environment(for: executable)
        environment["AGY_CLI_DISABLE_AUTO_UPDATE"] = "true"
        environment["BROWSER"] = "/usr/bin/false"
        environment["PATH"] = directory.path + ":" + (environment["PATH"] ?? "")
        guard let version = await runner.usageProbe(executable, arguments: ["--version"],
                                                    environment: environment, timeout: 3, workingDirectory: directory),
              Self.supportsUsage(version) else { return nil }
        return await runner.usageProbe(executable, arguments: ["-p", "/usage", "--output-format", "json"],
                                       environment: environment, timeout: 15, workingDirectory: directory)
    }

    static func supportsUsage(_ output: Data) -> Bool {
        let version = String(decoding: output, as: UTF8.self).trimmingCharacters(in: .whitespacesAndNewlines)
        let components = version.split(separator: ".", omittingEmptySubsequences: false)
        let parts = components.compactMap { Int($0) }
        guard components.count == 3, parts.count == 3,
              parts.allSatisfy({ $0 >= 0 }) else { return false }
        // Older versions can send /usage to a model as a literal prompt.
        return !parts.lexicographicallyPrecedes([1, 1, 11])
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
