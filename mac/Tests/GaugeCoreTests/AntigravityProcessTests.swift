import Darwin
import Foundation
import XCTest
@testable import GaugeCore

private func temporaryBundle(version: String? = "2.24.0", identifier: String = "com.google.antigravity",
                             engine: Bool = true) throws -> (root: URL, install: AntigravityInstall) {
    let root = FileManager.default.temporaryDirectory.appending(path: "AntigravityTests-\(UUID().uuidString)", directoryHint: .isDirectory)
    let bundle = root.appending(path: "Antigravity.app", directoryHint: .isDirectory)
    let bin = bundle.appending(path: "Contents/Resources/bin", directoryHint: .isDirectory)
    try FileManager.default.createDirectory(at: bin, withIntermediateDirectories: true)
    var plist: [String: Any] = ["CFBundleIdentifier": identifier]
    if let version { plist["CFBundleShortVersionString"] = version }
    try PropertyListSerialization.data(fromPropertyList: plist, format: .xml, options: 0)
        .write(to: bundle.appending(path: "Contents/Info.plist"))
    if engine {
        let path = bin.appending(path: "language_server").path
        FileManager.default.createFile(atPath: path, contents: Data("#!/bin/sh\n".utf8), attributes: [.posixPermissions: 0o755])
    }
    return (root, AntigravityInstall(bundle: bundle.resolvingSymlinksInPath()))
}

final class AntigravityInstallTests: XCTestCase {
    func testLocatesTheBundleAndReadsItsVersion() throws {
        let (root, expected) = try temporaryBundle()
        defer { try? FileManager.default.removeItem(at: root) }

        let missing = root.appending(path: "nowhere")
        let install = try XCTUnwrap(AntigravityInstall.locate(in: [missing, root]))
        XCTAssertEqual(install, expected)
        XCTAssertTrue(install.hasEngine)
        XCTAssertEqual(install.ideVersion, "2.24.0")
        XCTAssertTrue(install.engineExecutable.path.hasSuffix("Antigravity.app/Contents/Resources/bin/language_server"))
    }

    func testRejectsAnUnrelatedBundleAndReportsAMissingEngine() throws {
        let (other, _) = try temporaryBundle(identifier: "com.example.other")
        defer { try? FileManager.default.removeItem(at: other) }
        XCTAssertNil(AntigravityInstall.locate(in: [other]))

        let (noEngine, _) = try temporaryBundle(version: nil, engine: false)
        defer { try? FileManager.default.removeItem(at: noEngine) }
        let install = try XCTUnwrap(AntigravityInstall.locate(in: [noEngine]))
        XCTAssertFalse(install.hasEngine)
        XCTAssertNil(install.ideVersion)
    }

    func testContainsComparesWholePathComponents() {
        let install = AntigravityInstall(bundle: URL(fileURLWithPath: "/Applications/Antigravity.app"))
        XCTAssertTrue(install.contains("/Applications/Antigravity.app/Contents/Resources/bin/language_server"))
        XCTAssertFalse(install.contains("/Applications/Antigravity.app.bak/Contents/Resources/bin/language_server"))
        XCTAssertFalse(install.contains("/usr/local/bin/language_server"))
        XCTAssertFalse(install.contains("/Applications/Antigravity.app"))
    }
}

final class AntigravityProcessMatchTests: XCTestCase {
    private let install = AntigravityInstall(bundle: URL(fileURLWithPath: "/Applications/Antigravity.app"))
    private let engine = "/Applications/Antigravity.app/Contents/Resources/bin/language_server"

    func testReadsSpaceAndEqualsFlagForms() {
        let commandLine = AntigravityCommandLine(["--standalone", "--csrf_token", "abc", "--port=7", "--last"])
        XCTAssertEqual(commandLine.value(of: "--csrf_token"), "abc")
        XCTAssertEqual(commandLine.value(of: "--port"), "7")
        XCTAssertNil(commandLine.value(of: "--last"))
        XCTAssertNil(commandLine.value(of: "--missing"))
        XCTAssertTrue(commandLine.hasFlag("--standalone"))
        XCTAssertTrue(commandLine.hasFlag("--port"))
        // A prefix of another flag is not that flag.
        XCTAssertNil(AntigravityCommandLine(["--csrf_token_file", "x"]).value(of: "--csrf_token"))
    }

    func testDescriptionMasksSecrets() {
        let commandLine = AntigravityCommandLine(["--csrf_token", "secret-1", "--api_key=secret-2", "--app_data_dir", "my dir"])
        XCTAssertFalse(commandLine.description.contains("secret"))
        XCTAssertEqual(commandLine.description, #"--csrf_token *** --api_key=*** --app_data_dir "my dir""#)
        let server = AntigravityLanguageServer(pid: 42, csrfToken: "secret-3", ports: [1])
        XCTAssertFalse(server.description.contains("secret"))
    }

    func testAcceptsCurrentAndLegacyServerNamesInsideTheBundle() {
        let withToken = AntigravityCommandLine(["--standalone", "--csrf_token", "abc"])
        XCTAssertTrue(AntigravityProcessMatch.isCandidate(executablePath: engine, install: install, commandLine: withToken))
        XCTAssertTrue(AntigravityProcessMatch.isCandidate(
            executablePath: "/Applications/Antigravity.app/Contents/Resources/bin/language_server_macos_arm",
            install: install, commandLine: withToken))
    }

    func testRejectsServersWithoutTokenOutsideTheBundleOrWithOtherNames() {
        let withToken = AntigravityCommandLine(["--csrf_token", "abc"])
        XCTAssertFalse(AntigravityProcessMatch.isCandidate(executablePath: engine, install: install,
                                                           commandLine: AntigravityCommandLine(["--standalone"])))
        XCTAssertFalse(AntigravityProcessMatch.isCandidate(executablePath: engine, install: install,
                                                           commandLine: AntigravityCommandLine(["--csrf_token", " "])))
        for path in ["/usr/local/bin/language_server", "/Applications/Other.app/Contents/Resources/bin/language_server",
                     "/Applications/Antigravity.app/Contents/MacOS/Antigravity", "/X/notlanguage_server", "", nil] as [String?] {
            XCTAssertFalse(AntigravityProcessMatch.isCandidate(executablePath: path, install: install, commandLine: withToken),
                           path ?? "nil")
        }
        // Install not located: name + token is enough; a real server is not rejected outright.
        XCTAssertTrue(AntigravityProcessMatch.isCandidate(executablePath: "/anywhere/language_server", install: nil,
                                                          commandLine: withToken))
    }

    func testDiscoveryReadsTheTokenFromArgvAfterTheProgramName() {
        let candidate = AntigravityProcessCandidate(
            pid: 1, executablePath: engine, arguments: [engine, "--csrf_token", "tok", "--https_server_port", "0"])
        XCTAssertEqual(AntigravityProcessDiscovery.token(for: candidate, install: install), "tok")
        // argv[0] alone is not a flag/value pair.
        let bare = AntigravityProcessCandidate(pid: 1, executablePath: engine, arguments: ["--csrf_token"])
        XCTAssertNil(AntigravityProcessDiscovery.token(for: bare, install: install))
    }

    func testBuildsLaunchArgumentsMatchingTheAppWithOurTokenAndRandomPort() {
        let arguments = AntigravityEngineLaunch.arguments(csrfToken: "my-token", ideVersion: "2.24.0")
        let commandLine = AntigravityCommandLine(arguments)
        XCTAssertEqual(commandLine.value(of: "--csrf_token"), "my-token")
        XCTAssertEqual(commandLine.value(of: "--https_server_port"), "0")
        XCTAssertEqual(commandLine.value(of: "--app_data_dir"), "antigravity")
        XCTAssertEqual(commandLine.value(of: "--override_ide_version"), "2.24.0")
        XCTAssertEqual(commandLine.value(of: "--override_ide_name"), "antigravity")
        XCTAssertEqual(commandLine.value(of: "--override_user_agent_name"), "antigravity")
        XCTAssertEqual(commandLine.value(of: "--subclient_type"), "hub")
        XCTAssertEqual(commandLine.value(of: "--api_server_url"), "https://generativelanguage.googleapis.com")
        XCTAssertEqual(commandLine.value(of: "--cloud_code_endpoint"), "https://daily-cloudcode-pa.googleapis.com")
        for flag in ["--standalone", "--enable_sidecars", "--exit_on_stdin_close"] {
            XCTAssertTrue(commandLine.hasFlag(flag), flag)
        }
        XCTAssertFalse(AntigravityCommandLine(AntigravityEngineLaunch.arguments(csrfToken: "t", ideVersion: " "))
            .hasFlag("--override_ide_version"))
    }
}

final class DarwinProcessTableTests: XCTestCase {
    private func procArgs(argc: Int32, _ strings: [String], padding: Int = 3, trailing: [UInt8] = []) -> Data {
        var data = withUnsafeBytes(of: argc.littleEndian) { Data($0) }
        for (index, string) in strings.enumerated() {
            data.append(contentsOf: Array(string.utf8) + [0])
            if index == 0 { data.append(contentsOf: [UInt8](repeating: 0, count: padding)) }
        }
        data.append(contentsOf: trailing)
        return data
    }

    func testParsesExecutableArgumentsAndEnvironment() throws {
        let data = procArgs(argc: 3, ["/bin/ls", "ls", "--flag", "a value", "HOME=/Users/me", "MARK=1"], trailing: [0, 0])
        let parsed = try XCTUnwrap(DarwinProcessTable.parseProcArgs2(data))
        XCTAssertEqual(parsed.executable, "/bin/ls")
        XCTAssertEqual(parsed.arguments, ["ls", "--flag", "a value"])
        XCTAssertEqual(parsed.environment, ["HOME=/Users/me", "MARK=1"])
    }

    func testOmittedEnvironmentIsNilAndTruncatedDataIsRejected() {
        XCTAssertNil(DarwinProcessTable.parseProcArgs2(procArgs(argc: 1, ["/bin/ls", "ls"]))?.environment)
        XCTAssertNil(DarwinProcessTable.parseProcArgs2(procArgs(argc: 3, ["/bin/ls", "ls"])))
        XCTAssertNil(DarwinProcessTable.parseProcArgs2(Data([1, 0])))
    }

    func testReadsThisProcessAndFindsItsOwnLoopbackListener() throws {
        let pid = getpid()
        XCTAssertNotNil(DarwinProcessTable.executablePath(pid))
        XCTAssertNotNil(DarwinProcessTable.startTime(pid))
        XCTAssertEqual(DarwinProcessTable.uid(pid), getuid())
        XCTAssertTrue(DarwinProcessTable.allPIDs().contains(pid))
        XCTAssertFalse(try XCTUnwrap(DarwinProcessTable.arguments(pid)).arguments.isEmpty)

        let loopback = try listen(on: "127.0.0.1")
        defer { close(loopback.socket) }
        let anyAddress = try listen(on: "0.0.0.0")
        defer { close(anyAddress.socket) }

        let ports = DarwinProcessTable.loopbackListeningPorts(pid)
        XCTAssertTrue(ports.contains(loopback.port))
        // Only 127.0.0.1 counts: the loopback client and its TLS exception cover nothing else.
        XCTAssertFalse(ports.contains(anyAddress.port))
    }

    private func listen(on address: String) throws -> (socket: Int32, port: Int) {
        let fd = socket(AF_INET, SOCK_STREAM, 0)
        guard fd >= 0 else { throw POSIXError(.EBADF) }
        var addr = sockaddr_in()
        addr.sin_family = sa_family_t(AF_INET)
        addr.sin_port = 0
        inet_pton(AF_INET, address, &addr.sin_addr)
        var length = socklen_t(MemoryLayout<sockaddr_in>.size)
        let bound = withUnsafePointer(to: &addr) {
            $0.withMemoryRebound(to: sockaddr.self, capacity: 1) { Darwin.bind(fd, $0, length) }
        }
        guard bound == 0, Darwin.listen(fd, 1) == 0 else { close(fd); throw POSIXError(.EADDRINUSE) }
        _ = withUnsafeMutablePointer(to: &addr) {
            $0.withMemoryRebound(to: sockaddr.self, capacity: 1) { getsockname(fd, $0, &length) }
        }
        return (fd, Int(UInt16(bigEndian: addr.sin_port)))
    }
}

final class AntigravityEngineTests: XCTestCase {
    /// Spawn and teardown against a stand-in engine (`/bin/sleep`): a fresh process group, the
    /// marker in its environment, and nothing left behind.
    func testSpawnedProcessIsAGroupLeaderAndIsTornDown() async throws {
        let engine = try SpawnedEngine.spawn(executable: "/bin/sleep", arguments: ["30"],
                                             marker: ("AGENTGAUGE_TEST_MARKER", UUID().uuidString))
        XCTAssertEqual(getpgid(engine.pid), engine.pid)
        XCTAssertNil(engine.exitStatus())
        // Newer macOS releases hide other processes' environments from KERN_PROCARGS2, so the
        // marker can only be checked where the kernel still exposes it.
        if let environment = DarwinProcessTable.arguments(engine.pid)?.environment, !environment.isEmpty {
            XCTAssertTrue(environment.contains("\(engine.marker.name)=\(engine.marker.value)"))
        }

        await engine.terminate()

        XCTAssertEqual(kill(engine.pid, 0), -1) // reaped: the PID no longer names our child
        XCTAssertEqual(errno, ESRCH)
    }

    func testSpawnFailureIsReported() {
        XCTAssertThrowsError(try SpawnedEngine.spawn(executable: "/nonexistent/language_server", arguments: [],
                                                     marker: ("AGENTGAUGE_TEST_MARKER", "x")))
    }

    func testSerialGateRunsOneHolderAtATime() async {
        let gate = AntigravitySerialGate()
        let counter = Counter()
        await withTaskGroup(of: Void.self) { group in
            for _ in 0..<8 {
                group.addTask {
                    await gate.acquire()
                    counter.enter()
                    try? await Task.sleep(for: .milliseconds(5))
                    counter.leave()
                    gate.release()
                }
            }
        }
        XCTAssertEqual(counter.maximum, 1)
    }

    /// Live delegate read against the real install: `ANTIGRAVITY_LIVE=1 swift test --filter
    /// testLiveDelegateRead`. Prints windows and plan only, never a token.
    func testLiveDelegateRead() async throws {
        try XCTSkipUnless(ProcessInfo.processInfo.environment["ANTIGRAVITY_LIVE"] == "1", "set ANTIGRAVITY_LIVE=1")
        let install = try XCTUnwrap(AntigravityInstall.locate(), "Antigravity is not installed")
        let host = AntigravityEngineHost(install: install, client: AntigravityLoopbackClient())
        let started = Date()
        let reading = try await host.reading()
        let elapsed = Date().timeIntervalSince(started)
        let unwrapped = try XCTUnwrap(reading, "the engine returned no usage (signed out?)")
        let windows = try AntigravityQuotaParser.windows(from: unwrapped.quotaJSON)
        print("LIVE antigravity: \(windows.count) windows, plan=\(unwrapped.plan ?? "nil"), \(String(format: "%.1f", elapsed))s")
        for window in windows {
            print("LIVE  \(window.groupLabel ?? "-") / \(window.type.rawValue) id=\(window.id) used=\(String(format: "%.4f", window.usedRatio)) reset=\(window.resetTime.map { "\($0)" } ?? "nil")")
        }
        XCTAssertFalse(windows.isEmpty)
        let engine = install.engineExecutable.path
        let survivors = DarwinProcessTable.allPIDs().filter { DarwinProcessTable.executablePath($0) == engine }
        XCTAssertTrue(survivors.isEmpty, "language_server survived: \(survivors)")
    }
}

private final class Counter: @unchecked Sendable {
    private let lock = NSLock()
    private var current = 0
    private(set) var maximum = 0

    func enter() { lock.withLock { current += 1; maximum = max(maximum, current) } }
    func leave() { lock.withLock { current -= 1 } }
}
