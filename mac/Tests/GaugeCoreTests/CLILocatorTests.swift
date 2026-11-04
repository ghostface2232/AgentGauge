import XCTest
@testable import GaugeCore

final class CLILocatorTests: XCTestCase {
    private var root: URL!

    override func setUpWithError() throws {
        root = FileManager.default.temporaryDirectory.appending(path: "gauge-cli-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
    }

    override func tearDownWithError() throws {
        try? FileManager.default.removeItem(at: root)
    }

    @discardableResult
    private func executable(_ relative: String) throws -> URL {
        let url = root.appending(path: relative)
        try FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
        FileManager.default.createFile(atPath: url.path, contents: Data(), attributes: [.posixPermissions: 0o755])
        return url
    }

    func testClaudeDesktopBinaryPicksHighestVersionNumerically() throws {
        try executable("2.1.99/claude")
        let newest = try executable("2.1.309/9078b9c1d466/claude")
        XCTAssertEqual(CLILocator.newestClaudeBinary(in: root)?.path, newest.path)
    }

    func testClaudeDesktopBinaryInsideAppWrapperUnderBuildFolder() throws {
        // The layout the Claude desktop app writes (2.1.309).
        try executable("2.1.308/c17a819afb40/claude.app/Contents/MacOS/claude")
        let wrapped = try executable("2.1.309/5af865b9901c/claude.app/Contents/MacOS/claude")
        XCTAssertEqual(CLILocator.newestClaudeBinary(in: root)?.path, wrapped.path)
    }

    func testClaudeDesktopBinaryInsideAppWrapper() throws {
        let wrapped = try executable("2.1.309/claude.app/Contents/MacOS/claude")
        XCTAssertEqual(CLILocator.newestClaudeBinary(in: root)?.path, wrapped.path)
    }

    func testPathEntriesThatDependOnTheWorkingDirectoryAreIgnored() {
        XCTAssertEqual(CLILocator.absoluteDirectories([".", "bin", "../up", "", "~/tools", "/opt/tools", "/usr/bin"]),
                       ["/opt/tools", "/usr/bin"])
    }

    func testLocatorNeverReturnsABinaryFoundThroughARelativeDirectory() async throws {
        let planted = try executable("planted/claude")
        let previous = FileManager.default.currentDirectoryPath
        defer { FileManager.default.changeCurrentDirectoryPath(previous) }
        FileManager.default.changeCurrentDirectoryPath(planted.deletingLastPathComponent().path)

        let environment = await CLILocator(homeDirectory: root).environment(for: URL(fileURLWithPath: "/usr/bin/true"))

        XCTAssertFalse(environment["PATH", default: ""].split(separator: ":").contains { !$0.hasPrefix("/") })
    }

    func testCodexBinaryInsideChatGPTApp() throws {
        // The layout the Codex desktop app (ChatGPT.app, com.openai.codex) ships.
        let binary = try executable("Applications/ChatGPT.app/Contents/Resources/codex-cli/bin/codex")
        try executable("Applications/ChatGPT.app/Contents/Resources/codex-cli/codex-path/rg")
        let roots = [root.appending(path: "System").path, root.appending(path: "Applications").path]
        XCTAssertEqual(CLILocator.bundledCodexBinary(inApplications: roots)?.path, binary.path)
    }

    func testCodexBinaryIgnoresDirectoryOfTheSameName() throws {
        try FileManager.default.createDirectory(
            at: root.appending(path: "Applications/Codex.app/Contents/Resources/codex"), withIntermediateDirectories: true)
        XCTAssertNil(CLILocator.bundledCodexBinary(inApplications: [root.appending(path: "Applications").path]))
    }

    func testClaudeDesktopBinarySkipsVersionWithoutExecutable() throws {
        let older = try executable("2.1.308/claude")
        try FileManager.default.createDirectory(at: root.appending(path: "2.1.309/partial"), withIntermediateDirectories: true)
        XCTAssertEqual(CLILocator.newestClaudeBinary(in: root)?.path, older.path)
    }

    func testClaudeDesktopBinaryMissingRoot() {
        XCTAssertNil(CLILocator.newestClaudeBinary(in: root.appending(path: "absent")))
    }

    func testClaudeDesktopBinaryStillDownloadingLosesToOlderVerifiedBuild() throws {
        let older = try executable("2.1.308/c17a819afb40/claude.app/Contents/MacOS/claude")
        FileManager.default.createFile(atPath: root.appending(path: "2.1.308/c17a819afb40/.verified").path, contents: Data())
        try executable("2.1.309/9078b9c1d466/claude.app/Contents/MacOS/claude")
        XCTAssertEqual(CLILocator.newestClaudeBinary(in: root)?.path, older.path)
    }

    func testClaudeDesktopBinaryPrefersVerifiedBuildWithinOneVersion() throws {
        try executable("2.1.309/aaaa/claude")
        let verified = try executable("2.1.309/bbbb/claude")
        FileManager.default.createFile(atPath: root.appending(path: "2.1.309/bbbb/.verified").path, contents: Data())
        XCTAssertEqual(CLILocator.newestClaudeBinary(in: root)?.path, verified.path)
    }

    func testClaudeDesktopBinaryNewestVerifiedWins() throws {
        try executable("2.1.99/aaaa/claude")
        FileManager.default.createFile(atPath: root.appending(path: "2.1.99/aaaa/.verified").path, contents: Data())
        let newest = try executable("2.1.309/bbbb/claude")
        FileManager.default.createFile(atPath: root.appending(path: "2.1.309/bbbb/.verified").path, contents: Data())
        XCTAssertEqual(CLILocator.newestClaudeBinary(in: root)?.path, newest.path)
    }

    func testClaudeDesktopBinaryWithoutAnyMarkerFallsBackToNewest() throws {
        try executable("2.1.308/aaaa/claude")
        let newest = try executable("2.1.309/bbbb/claude")
        XCTAssertEqual(CLILocator.newestClaudeBinary(in: root)?.path, newest.path)
    }

    // MARK: Login shell probe

    private final class ShellRunner: ProcessRunning, @unchecked Sendable {
        private let lock = NSLock()
        private var results: [ProcessResult]
        private var count = 0
        private let delay: Duration

        init(_ results: [ProcessResult], delay: Duration = .zero) {
            self.results = results
            self.delay = delay
        }

        var runs: Int { lock.withLock { count } }

        func run(_ executable: URL, arguments: [String], environment: [String: String],
                 timeout: TimeInterval, captureOutput: Bool) async -> ProcessResult {
            let result = lock.withLock { () -> ProcessResult in
                count += 1
                return results.isEmpty ? ProcessResult.launchFailed : results.removeFirst()
            }
            try? await Task.sleep(for: delay)
            return result
        }
    }

    private func probeAnswer(_ path: String) -> ProcessResult {
        ProcessResult(exitCode: 0, timedOut: false, launched: true,
                      output: Data("__AGENTGAUGE_PATH__\(path)__AGENTGAUGE_END__".utf8))
    }

    private func path(_ locator: CLILocator) async -> [Substring] {
        await locator.environment(for: URL(fileURLWithPath: "/usr/bin/true"))["PATH", default: ""].split(separator: ":")
    }

    func testFailedShellProbeIsRetriedAfterItsCooldownAndOnlySuccessIsKept() async {
        let timedOut = ProcessResult(exitCode: nil, timedOut: true, launched: true, output: Data())
        let runner = ShellRunner([timedOut, probeAnswer("/opt/probed/bin")])
        let clock = ManualClock()
        let locator = CLILocator(runner: runner, homeDirectory: root, clock: clock)

        var entries = await path(locator)
        XCTAssertFalse(entries.contains("/opt/probed/bin"))
        XCTAssertEqual(runner.runs, 1)

        entries = await path(locator)
        XCTAssertEqual(runner.runs, 1, "inside the cooldown a failed probe is not repeated")

        clock.advance(CLILocator.shellProbeRetryCooldown + 1)
        entries = await path(locator)
        XCTAssertTrue(entries.contains("/opt/probed/bin"))
        XCTAssertEqual(runner.runs, 2)

        clock.advance(CLILocator.shellProbeRetryCooldown * 10)
        entries = await path(locator)
        XCTAssertTrue(entries.contains("/opt/probed/bin"))
        XCTAssertEqual(runner.runs, 2, "a good answer is kept for the process lifetime")
    }

    func testConcurrentCallersShareOneShellProbe() async {
        let runner = ShellRunner([probeAnswer("/opt/probed/bin")], delay: .milliseconds(100))
        let locator = CLILocator(runner: runner, homeDirectory: root, clock: ManualClock())
        async let first = path(locator)
        async let second = path(locator)
        let entries = await (first, second)
        XCTAssertTrue(entries.0.contains("/opt/probed/bin"))
        XCTAssertTrue(entries.1.contains("/opt/probed/bin"))
        XCTAssertEqual(runner.runs, 1)
    }

    func testLoginShellPathParsing() {
        XCTAssertEqual(CLILocator.parseLoginShellPath(Data("rc noise\n__AGENTGAUGE_PATH__/a:/b__AGENTGAUGE_END__".utf8)), ["/a", "/b"])
        XCTAssertNil(CLILocator.parseLoginShellPath(nil))
        XCTAssertNil(CLILocator.parseLoginShellPath(Data()))
        XCTAssertNil(CLILocator.parseLoginShellPath(Data("__AGENTGAUGE_PATH__/a".utf8)), "cut off before the end marker")
        XCTAssertNil(CLILocator.parseLoginShellPath(Data("__AGENTGAUGE_PATH____AGENTGAUGE_END__".utf8)), "an empty PATH is no answer")
    }
}
