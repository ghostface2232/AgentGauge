import Foundation
import XCTest
@testable import GaugeCore

/// A full scan of this Mac's real Claude Code and Codex logs into a throwaway ledger,
/// reporting timings and totals (never log contents). Opt-in, because it reads the
/// developer's own logs: `AGENTGAUGE_LIVE_APICOST=1 swift test --filter ApiCostLiveTests`.
/// `AGENTGAUGE_LIVE_APICOST_DAY=yyyy-MM-dd` also prints that day's Claude rows for a
/// cross-check against an independent recomputation.
final class ApiCostLiveTests: XCTestCase {
    func testScansTheRealLogs() throws {
        let environment = ProcessInfo.processInfo.environment
        guard environment["AGENTGAUGE_LIVE_APICOST"] == "1" else {
            throw XCTSkip("Set AGENTGAUGE_LIVE_APICOST=1 to scan this machine's real logs.")
        }
        let dir = FileManager.default.temporaryDirectory.appending(path: "GaugeApiCostLive-\(UUID().uuidString)", directoryHint: .isDirectory)
        defer { try? FileManager.default.removeItem(at: dir) }
        let scanner = ApiCostScanner(ledgerDirectory: dir)
        let tools: Set<ToolKind> = [.claude, .codex]

        var started = Date()
        var passes = 0
        var result: ApiCostScanner.Result
        repeat {
            result = try scanner.scan(tools: tools)
            passes += 1
        } while !result.isComplete && passes < ApiCostService.maxPassesPerScan
        let firstSeconds = Date().timeIntervalSince(started)
        let ledger = ApiCostLedger.load(from: dir.appending(path: ApiCostScanner.ledgerFileName))
        let bytes = ledger.files.values.reduce(Int64(0)) { $0 + $1.parsedBytes }

        started = Date()
        let second = try scanner.scan(tools: tools)
        let secondSeconds = Date().timeIntervalSince(started)
        // Not compared with the first result: a CLI running alongside (this very session,
        // say) keeps appending to the logs between the two scans.
        XCTAssertTrue(second.isComplete)

        print(String(format: "LIVE first pass: %.2f s over %d pass(es), %.1f MB in %d files; incremental: %.3f s",
                     firstSeconds, passes, Double(bytes) / 1_048_576, ledger.files.count, secondSeconds))
        let ledgerSize = (try? FileManager.default.attributesOfItem(atPath: dir.appending(path: ApiCostScanner.ledgerFileName).path)[.size] as? Int) ?? 0
        print("LIVE ledger size: \(ledgerSize) bytes, \(ledger.keys.count) keys")
        for tool in [ToolKind.claude, .codex] {
            guard let estimate = second.estimates[tool] else { continue }
            let t = estimate.pricedTokens
            print("LIVE \(tool.rawValue) \(estimate.year)-\(estimate.month): \(estimate.costUSD) USD, chip \(ApiCostText.chip(estimate) ?? "none"), "
                  + "input \(t.input) cacheRead \(t.cacheRead) cacheWrite \(t.cacheWrite) cacheWrite1h \(t.cacheWrite1h) output \(t.output), "
                  + "unpriced \(estimate.unpricedTokens) \(estimate.unpricedModels)")
        }

        if let day = environment["AGENTGAUGE_LIVE_APICOST_DAY"] {
            var byModel: [String: TokenTotals] = [:]
            var cost = Decimal(0)
            for row in ledger.files.values.filter({ $0.tool == ToolKind.claude.rawValue }).flatMap(\.days) where row.day == day {
                byModel[row.model, default: .zero] = byModel[row.model, default: .zero] + row.tokens
                cost += ApiCostPricing.cost(row.model, row.tokens, longContext: row.longContext) ?? 0
            }
            for (model, t) in byModel.sorted(by: { $0.key < $1.key }) {
                print("LIVE day \(day) \(model): input \(t.input) cacheRead \(t.cacheRead) cacheWrite \(t.cacheWrite) "
                      + "cacheWrite1h \(t.cacheWrite1h) output \(t.output)")
            }
            print("LIVE day \(day) claude cost: \(cost)")
        }
    }
}
