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
}
