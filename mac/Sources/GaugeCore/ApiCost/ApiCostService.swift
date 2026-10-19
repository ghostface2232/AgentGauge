import Foundation
import os

/// Runs the `ApiCostScanner` off the cooperative pool while the API-cost option is on and
/// hands each complete result to the listener. Scans happen on `start()`, every fifteen
/// minutes after that, when the popover opens — debounced to one per minute, since the logs
/// only change while a CLI is working — and at once when the set of shown tools changes.
/// While stopped nothing is read at all; the ledger on disk is simply left where it is.
///
/// The listener runs on this actor, never with a budget-limited (undercounting) result,
/// and never after `stop()`; the app hops to the main actor itself.
public actor ApiCostService {
    public typealias Listener = @Sendable ([ToolKind: ApiCostEstimate]) -> Void

    public static let interval: TimeInterval = 15 * 60
    public static let openDebounce: TimeInterval = 60
    /// A backstop, not an expected limit: each pass reads up to 512 MB.
    static let maxPassesPerScan = 64

    private let scanner: ApiCostScanner
    private let clock: any MonotonicClock
    private let interval: TimeInterval
    private var listener: Listener?
    private var tools: Set<ToolKind> = []
    private var active: ScanCancellation?
    private var loop: Task<Void, Never>?
    private var scanning = false
    // Set when a scan was asked for while another was running; the running one re-runs once
    // it finishes, so stopping and starting mid-scan still produces a fresh result.
    private var rerunRequested = false
    private var lastScanStart: TimeInterval?

    public init(scanner: ApiCostScanner = ApiCostScanner(), clock: any MonotonicClock = SystemMonotonicClock(),
                interval: TimeInterval = ApiCostService.interval) {
        self.scanner = scanner
        self.clock = clock
        self.interval = interval
    }

    public func setListener(_ listener: @escaping Listener) {
        self.listener = listener
    }

    public var isRunning: Bool { active != nil }

    /// Starts scanning: one scan now, then every `interval`. No-op while running.
    public func start() {
        guard active == nil else { return }
        let token = ScanCancellation()
        active = token
        let interval = interval
        loop = Task { [weak self] in
            await self?.scanOnce(token)
            while !Task.isCancelled {
                try? await Task.sleep(for: .seconds(interval))
                guard !Task.isCancelled, let self else { return }
                await self.scanOnce(token)
            }
        }
    }

    /// Stops scanning and cancels a running scan at its next line; nothing it found is
    /// published.
    public func stop() {
        active?.cancel()
        active = nil
        loop?.cancel()
        loop = nil
    }

    /// The shown tools. Only tools with local logs matter; when that set changes while
    /// running, a scan starts at once — past the popover debounce — so a tool just shown
    /// gets its chip now rather than on the timer. The returned task finishes with the scan.
    @discardableResult
    public func setTools(_ tools: Set<ToolKind>) -> Task<Void, Never>? {
        let relevant = tools.filter(\.hasLocalCostLogs)
        guard relevant != self.tools else { return nil }
        self.tools = relevant
        return requestScan()
    }

    /// A popover open: scan unless the last scan started under a minute ago. The returned
    /// task finishes with the scan.
    @discardableResult
    public func popoverOpened() -> Task<Void, Never>? {
        if let last = lastScanStart, clock.now() - last < Self.openDebounce { return nil }
        return requestScan()
    }

    private func requestScan() -> Task<Void, Never>? {
        guard let token = active else { return nil }
        return Task { await self.scanOnce(token) }
    }

    private func scanOnce(_ token: ScanCancellation) async {
        guard !token.isCancelled else { return }
        guard !scanning else {
            rerunRequested = true
            return
        }
        scanning = true
        lastScanStart = clock.now()
        let outcome = await Self.run(scanner, tools: tools, token: token)
        scanning = false

        switch outcome {
        case .success(let estimates?):
            // Checked again here, where `stop()` runs: a result that finished just after
            // the option was switched off must not bring the chips back.
            if !token.isCancelled { listener?(estimates) }
        case .success(nil), .failure(is CancellationError):
            break
        case .failure(let error):
            // The estimate is a convenience; nothing about usage display depends on it.
            // Type only: an IO error's description embeds a transcript path.
            Log.write("cost", "Scan failed: \(type(of: error))")
        }

        if rerunRequested {
            rerunRequested = false
            if let current = active { await scanOnce(current) }
        }
    }

    // A budget-limited scan undercounts; showing it would put a number on screen that later
    // jumps. Scan again (each pass resumes where the last stopped and saves its progress)
    // and return a result only once a pass completes. Blocking file IO runs on a dispatch
    // queue rather than holding a cooperative-pool thread for up to 20 s per pass.
    private static func run(_ scanner: ApiCostScanner, tools: Set<ToolKind>,
                            token: ScanCancellation) async -> Result<[ToolKind: ApiCostEstimate]?, any Error> {
        await withCheckedContinuation { continuation in
            DispatchQueue.global(qos: .utility).async {
                do {
                    var result = try scanner.scan(tools: tools, isCancelled: { token.isCancelled })
                    var pass = 1
                    while !result.isComplete && pass < maxPassesPerScan {
                        result = try scanner.scan(tools: tools, isCancelled: { token.isCancelled })
                        pass += 1
                    }
                    continuation.resume(returning: .success(result.isComplete ? result.estimates : nil))
                } catch {
                    continuation.resume(returning: .failure(error))
                }
            }
        }
    }
}

/// A cancellation flag the blocking scan polls per line.
final class ScanCancellation: Sendable {
    private let cancelled = OSAllocatedUnfairLock(initialState: false)

    var isCancelled: Bool { cancelled.withLock { $0 } }

    func cancel() { cancelled.withLock { $0 = true } }
}
