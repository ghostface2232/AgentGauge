import Foundation
import XCTest
@testable import GaugeCore

/// Runs real child processes, so these also exercise Foundation's `Process` and the pipe.
final class SystemProcessRunnerTests: XCTestCase {
    private let runner = SystemProcessRunner()
    private let shell = URL(fileURLWithPath: "/bin/sh")
    private let environment = ["PATH": "/usr/bin:/bin"]

    private func sh(_ script: String, timeout: TimeInterval = 30) async -> ProcessResult {
        await runner.run(shell, arguments: ["-c", script], environment: environment, timeout: timeout, captureOutput: true)
    }

    private func text(_ result: ProcessResult) -> String? {
        result.output.map { String(decoding: $0, as: UTF8.self) }
    }

    /// The login-shell probe's hazard: a background daemon started from ~/.zshrc inherits stdout.
    func testDescendantHoldingThePipeDoesNotHoldTheResult() async {
        let started = Date()
        let result = await sh("sleep 20 & printf hello")
        XCTAssertEqual(text(result), "hello")
        XCTAssertEqual(result.exitCode, 0)
        XCTAssertFalse(result.timedOut)
        XCTAssertLessThan(Date().timeIntervalSince(started), 10, "must end at the grace period, not at the descendant's exit")
    }

    func testFastExitKeepsItsOutput() async {
        for _ in 0..<25 {
            let result = await runner.run(URL(fileURLWithPath: "/bin/echo"), arguments: ["hi"], environment: environment,
                                          timeout: 30, captureOutput: true)
            XCTAssertEqual(text(result), "hi\n")
        }
    }

    func testOutputLargerThanThePipeBufferIsDrained() async {
        let result = await sh("head -c 300000 /dev/zero")
        XCTAssertEqual(result.output?.count, 300_000)
        XCTAssertEqual(result.exitCode, 0)
        XCTAssertFalse(result.timedOut)
    }

    func testTimeoutEndsTheProcessAndKeepsPartialOutput() async {
        let started = Date()
        let result = await sh("printf partial; exec sleep 30", timeout: 0.3)
        XCTAssertTrue(result.timedOut)
        XCTAssertEqual(text(result), "partial")
        XCTAssertLessThan(Date().timeIntervalSince(started), 10)
    }

    func testProcessIgnoringSIGTERMIsKilledAndItsLeftoverChildCannotHoldThePipe() async {
        let started = Date()
        let result = await sh("trap '' TERM; printf x; while :; do sleep 1; done", timeout: 0.3)
        XCTAssertTrue(result.timedOut)
        XCTAssertEqual(text(result), "x")
        XCTAssertLessThan(Date().timeIntervalSince(started), 10)
    }

    func testTimeoutWithoutCapture() async {
        let result = await runner.run(URL(fileURLWithPath: "/bin/sleep"), arguments: ["30"], environment: environment,
                                      timeout: 0.2, captureOutput: false)
        XCTAssertTrue(result.timedOut)
        XCTAssertNil(result.output)
    }

    func testLaunchFailureReturnsWithACapturePending() async {
        let result = await runner.run(URL(fileURLWithPath: "/nonexistent/tool"), arguments: [], environment: environment,
                                      timeout: 30, captureOutput: true)
        XCTAssertFalse(result.launched)
        XCTAssertNil(result.output)
    }
}
