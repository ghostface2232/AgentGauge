import Foundation
import XCTest
@testable import GaugeCore

private final class StepClock: MonotonicClock, @unchecked Sendable {
    private let lock = NSLock()
    private var value: TimeInterval = 1_000

    func now() -> TimeInterval { lock.withLock { value } }
    func advance(_ seconds: TimeInterval) { lock.withLock { value += seconds } }
}

private final class Published: @unchecked Sendable {
    private let lock = NSLock()
    private var results: [[ToolKind: ApiCostEstimate]] = []

    func append(_ result: [ToolKind: ApiCostEstimate]) { lock.withLock { results.append(result) } }
    var all: [[ToolKind: ApiCostEstimate]] { lock.withLock { results } }
}

/// The service's cadence: what starts a scan, what is debounced, and that only a complete
/// result from a still-running service reaches the listener.
final class ApiCostServiceTests: XCTestCase {
    private var dir: URL!
    private let clock = StepClock()

    override func setUpWithError() throws {
        dir = FileManager.default.temporaryDirectory.appending(path: "GaugeApiCostService-\(UUID().uuidString)", directoryHint: .isDirectory)
        let folder = dir.appending(path: "claude/project", directoryHint: .isDirectory)
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        for (name, id) in [("a.jsonl", "m1"), ("b.jsonl", "m2")] {
            let line = "{\"type\":\"assistant\",\"timestamp\":\"2026-09-10T10:00:00Z\",\"requestId\":\"r\",\"message\":{\"id\":\"\(id)\","
                + "\"model\":\"claude-opus-5\",\"stop_reason\":\"end_turn\",\"usage\":{\"input_tokens\":1000000,"
                + "\"cache_read_input_tokens\":0,\"output_tokens\":0}}}\n"
            try Data(line.utf8).write(to: folder.appending(path: name))
        }
    }

    override func tearDownWithError() throws {
        try? FileManager.default.removeItem(at: dir)
    }

    private func makeService(maxBytes: Int64 = ApiCostScanner.defaultMaxBytesPerScan) -> ApiCostService {
        let scanner = ApiCostScanner(
            ledgerDirectory: dir,
            sources: [ApiCostScanner.LogSource(tool: .claude, kind: .claude, roots: [dir.appending(path: "claude")])],
            timeZone: TimeZone(identifier: "UTC")!, maxBytesPerScan: maxBytes,
            now: { Timestamp.parse("2026-09-18T12:00:00Z")! })
        // A long interval so the periodic loop never fires during a test.
        return ApiCostService(scanner: scanner, clock: clock, interval: 3600)
    }

    func testStartScansAndPublishesOnlyTheCompleteResult() async throws {
        // A one-byte budget needs several passes; the listener sees only the last.
        let service = makeService(maxBytes: 1)
        let published = Published()
        let done = expectation(description: "published")
        await service.setListener { published.append($0); done.fulfill() }
        await service.setTools([.claude, .cursor])
        await service.start()
        await fulfillment(of: [done], timeout: 10)
        await service.stop()

        XCTAssertEqual(published.all.count, 1)
        XCTAssertEqual(published.all.first?[.claude]?.costUSD, 10)
        XCTAssertNil(published.all.first?[.cursor])
    }

    func testPopoverOpensAreDebouncedToOnePerMinute() async throws {
        let service = makeService()
        let published = Published()
        await service.setListener { published.append($0) }
        await service.setTools([.claude])
        await service.start()
        // The start scan holds the debounce window; wait for it to land.
        while published.all.isEmpty { try await Task.sleep(for: .milliseconds(10)) }

        let debounced = await service.popoverOpened()
        XCTAssertNil(debounced)
        clock.advance(ApiCostService.openDebounce)
        await service.popoverOpened()?.value
        XCTAssertEqual(published.all.count, 2)
        await service.stop()
    }

    func testAChangeOfShownToolsScansAtOncePastTheDebounce() async throws {
        let service = makeService()
        let published = Published()
        await service.setListener { published.append($0) }
        await service.start()
        while published.all.isEmpty { try await Task.sleep(for: .milliseconds(10)) }
        XCTAssertEqual(published.all.last?.isEmpty, true)

        // Showing Cursor changes nothing that has local logs.
        let ignored = await service.setTools([.cursor])
        XCTAssertNil(ignored)
        await service.setTools([.claude, .cursor])?.value
        XCTAssertEqual(published.all.last?[.claude]?.costUSD, 10)
        await service.stop()
    }

    func testNothingIsScannedOrPublishedWhileStopped() async throws {
        let service = makeService()
        let published = Published()
        await service.setListener { published.append($0) }
        let beforeStart = await service.setTools([.claude])
        XCTAssertNil(beforeStart)
        let open = await service.popoverOpened()
        XCTAssertNil(open)

        await service.start()
        await service.stop()
        try await Task.sleep(for: .milliseconds(300))
        // A scan that finished after stop() must not publish.
        XCTAssertTrue(published.all.isEmpty)
        let running = await service.isRunning
        XCTAssertFalse(running)
    }
}
