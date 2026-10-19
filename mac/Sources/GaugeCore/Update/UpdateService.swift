import CryptoKit
import Foundation

/// A dotted numeric version ("0.4.2"); a pre-release suffix is ignored, missing parts are 0.
public struct AppVersion: Sendable, Comparable, CustomStringConvertible {
    public let parts: [Int]

    public init?(_ text: String) {
        var trimmed = text.trimmingCharacters(in: .whitespaces)
        if trimmed.first == "v" || trimmed.first == "V" { trimmed.removeFirst() }
        if let dash = trimmed.firstIndex(of: "-") { trimmed = String(trimmed[..<dash]) }
        let numbers = trimmed.split(separator: ".", omittingEmptySubsequences: false).map { Int($0) }
        guard (1...4).contains(numbers.count), numbers.allSatisfy({ ($0 ?? -1) >= 0 }) else { return nil }
        var parts = numbers.compactMap { $0 }
        while parts.count < 3 { parts.append(0) }
        self.parts = parts
    }

    public var description: String { parts.prefix(3).map(String.init).joined(separator: ".") }

    public static func < (lhs: AppVersion, rhs: AppVersion) -> Bool {
        let count = max(lhs.parts.count, rhs.parts.count)
        for index in 0..<count {
            let left = index < lhs.parts.count ? lhs.parts[index] : 0
            let right = index < rhs.parts.count ? rhs.parts[index] : 0
            if left != right { return left < right }
        }
        return false
    }

    public static func == (lhs: AppVersion, rhs: AppVersion) -> Bool { !(lhs < rhs) && !(rhs < lhs) }
}

/// A published release with the macOS asset to download and the lowercase hex SHA-256 GitHub
/// computed for that asset on upload.
public struct AppRelease: Sendable, Equatable {
    public let version: AppVersion
    public let tag: String
    public let downloadURL: URL
    public let sha256: String
}

public enum UpdateCheck: Sendable, Equatable {
    case upToDate
    case available(AppRelease)
    case failed
}

public enum UpdateInstallError: Error, Sendable {
    case download
    case digestMismatch
    case invalidArchive
    case notWritable
    case launchFailed
}

/// Checks GitHub Releases for a newer build and installs it in place. The release carries
/// `AgentGauge-macOS.zip` (a universal build) beside the Windows installer; its GitHub
/// `digest` is verified before anything is unpacked, and the unpacked bundle must be this
/// app (same bundle id) at the release's version and pass `codesign --verify` before it may
/// replace the running copy. The digest proves integrity against transport and CDN faults,
/// not authenticity — it comes from the same API response as the URL.
///
/// The swap itself is done by a small detached shell helper that waits for this process to
/// exit, moves the old bundle aside, moves the new one in, restores the old one if that
/// fails, and relaunches with `--updated`. Applying is always a deliberate click.
public struct UpdateService: Sendable {
    public static let assetName = "AgentGauge-macOS.zip"
    static let latestURL = URL(string: "https://api.github.com/repos/ghostface2232/AgentGauge/releases/latest")!

    public let currentVersion: AppVersion
    private let transport: any HTTPTransport
    private let runner: any ProcessRunning
    private let bundleIdentifier: String

    public init(currentVersion: AppVersion, bundleIdentifier: String,
                transport: any HTTPTransport = UpdateService.makeTransport(),
                runner: any ProcessRunning = SystemProcessRunner()) {
        self.currentVersion = currentVersion
        self.bundleIdentifier = bundleIdentifier
        self.transport = transport
        self.runner = runner
    }

    /// A dedicated session: GitHub requires a User-Agent, and the download needs a longer
    /// timeout than the usage reads allow.
    public static func makeTransport() -> any HTTPTransport {
        let configuration = URLSessionConfiguration.ephemeral
        configuration.timeoutIntervalForRequest = 30
        configuration.timeoutIntervalForResource = 300
        configuration.requestCachePolicy = .reloadIgnoringLocalCacheData
        configuration.httpAdditionalHeaders = ["User-Agent": "AgentGauge-Updater"]
        return URLSessionTransport(session: URLSession(configuration: configuration))
    }

    /// Never throws: failures map to `.failed` (and are logged).
    public func check() async -> UpdateCheck {
        do {
            var request = URLRequest(url: Self.latestURL)
            request.setValue("application/vnd.github+json", forHTTPHeaderField: "Accept")
            let (data, response) = try await transport.send(request)
            try HTTPStatus.ensureSuccess(response, now: Date())
            return Self.evaluate(data, current: currentVersion)
        } catch {
            Log.write("update", "Release check failed: \(type(of: error))")
            return .failed
        }
    }

    static func evaluate(_ data: Data, current: AppVersion) -> UpdateCheck {
        guard let root = try? JSONObject.parse(data),
              let tag = root.string("tag_name"), let version = AppVersion(tag) else { return .failed }
        let asset = root.objects("assets")?.first { $0.string("name")?.lowercased() == assetName.lowercased() }
        guard let asset, let link = asset.string("browser_download_url"), let url = URL(string: link),
              url.scheme == "https" else {
            // Releases before the Mac asset existed: nothing to offer, which is not an error
            // when this build is already as new.
            return version <= current ? .upToDate : .failed
        }
        guard let sha256 = parseDigest(asset.string("digest")) else {
            if version <= current { return .upToDate }
            Log.write("update", "Release \(tag): macOS asset has no usable sha256 digest; not offered")
            return .failed
        }
        guard version > current else { return .upToDate }
        return .available(AppRelease(version: version, tag: tag, downloadURL: url, sha256: sha256))
    }

    /// GitHub's asset digest, "sha256:<64 hex>", as lowercase hex; anything else is unusable.
    static func parseDigest(_ digest: String?) -> String? {
        guard let digest, digest.hasPrefix("sha256:") else { return nil }
        let hex = digest.dropFirst("sha256:".count).lowercased()
        guard hex.count == 64, hex.allSatisfy({ $0.isHexDigit }) else { return nil }
        return String(hex)
    }

    /// Downloads, verifies and unpacks the release next to the installed app, then starts the
    /// helper that swaps it in once this process exits. On success the caller must terminate.
    public func install(_ release: AppRelease, replacing installed: URL, processID: Int32) async throws {
        let parent = installed.deletingLastPathComponent()
        guard FileManager.default.isWritableFile(atPath: parent.path),
              FileManager.default.isWritableFile(atPath: installed.path) else {
            throw UpdateInstallError.notWritable
        }

        let work = FileManager.default.temporaryDirectory
            .appending(path: "AgentGauge-update-\(UUID().uuidString)", directoryHint: .isDirectory)
        try FileManager.default.createDirectory(at: work, withIntermediateDirectories: true)
        var keepWork = false
        defer { if !keepWork { try? FileManager.default.removeItem(at: work) } }

        let archive = work.appending(path: Self.assetName)
        do {
            let (data, response) = try await transport.send(URLRequest(url: release.downloadURL))
            try HTTPStatus.ensureSuccess(response, now: Date())
            // Never unpack bytes that are not the release's.
            let actual = SHA256.hash(data: data).map { String(format: "%02x", $0) }.joined()
            guard actual == release.sha256 else {
                Log.write("update", "Download for \(release.tag) failed sha256 verification; not installed")
                throw UpdateInstallError.digestMismatch
            }
            try data.write(to: archive)
        } catch let error as UpdateInstallError {
            throw error
        } catch {
            Log.write("update", "Download failed: \(type(of: error))")
            throw UpdateInstallError.download
        }

        let unpacked = work.appending(path: "unpacked", directoryHint: .isDirectory)
        let ditto = await runner.run(URL(fileURLWithPath: "/usr/bin/ditto"), arguments: ["-x", "-k", archive.path, unpacked.path],
                                     environment: [:], timeout: 120, captureOutput: false)
        let app = unpacked.appending(path: "AgentGauge.app", directoryHint: .isDirectory)
        guard ditto.exitCode == 0, isExpectedBundle(app, version: release.version) else {
            Log.write("update", "Unpacked update is not a valid AgentGauge \(release.tag) bundle")
            throw UpdateInstallError.invalidArchive
        }
        let verify = await runner.run(URL(fileURLWithPath: "/usr/bin/codesign"), arguments: ["--verify", "--deep", "--strict", app.path],
                                      environment: [:], timeout: 60, captureOutput: false)
        guard verify.exitCode == 0 else {
            Log.write("update", "Update bundle failed codesign verification")
            throw UpdateInstallError.invalidArchive
        }

        // Staged beside the installed copy so the final move is a same-volume rename.
        let staged = parent.appending(path: ".AgentGauge-update-\(release.version).app", directoryHint: .isDirectory)
        try? FileManager.default.removeItem(at: staged)
        do {
            try FileManager.default.moveItem(at: app, to: staged)
        } catch {
            throw UpdateInstallError.notWritable
        }

        let helper = work.appending(path: "swap.sh")
        try Self.helperScript.write(to: helper, atomically: true, encoding: .utf8)
        let process = Process()
        process.executableURL = URL(fileURLWithPath: "/bin/sh")
        // Paths travel as arguments, never interpolated into the script text.
        process.arguments = [helper.path, String(processID), installed.path, staged.path, work.path]
        process.standardInput = FileHandle.nullDevice
        process.standardOutput = FileHandle.nullDevice
        process.standardError = FileHandle.nullDevice
        do {
            try process.run()
            keepWork = true
        } catch {
            try? FileManager.default.removeItem(at: staged)
            throw UpdateInstallError.launchFailed
        }
    }

    private func isExpectedBundle(_ app: URL, version: AppVersion) -> Bool {
        guard let info = NSDictionary(contentsOf: app.appending(path: "Contents/Info.plist")) as? [String: Any],
              info["CFBundleIdentifier"] as? String == bundleIdentifier,
              let shortVersion = info["CFBundleShortVersionString"] as? String,
              AppVersion(shortVersion) == version else { return false }
        return FileManager.default.isExecutableFile(atPath: app.appending(path: "Contents/MacOS/AgentGauge").path)
    }

    /// $1 pid to wait for, $2 installed bundle, $3 staged bundle, $4 work directory (removed).
    /// The old copy is kept until the new one is in place, and put back if the move fails.
    static let helperScript = """
    #!/bin/sh
    pid="$1"; target="$2"; staged="$3"; work="$4"
    i=0
    while kill -0 "$pid" 2>/dev/null && [ "$i" -lt 300 ]; do sleep 0.2; i=$((i + 1)); done
    backup="$target.previous"
    rm -rf "$backup"
    if mv "$target" "$backup"; then
        if mv "$staged" "$target"; then
            rm -rf "$backup"
            open "$target" --args --updated
        else
            mv "$backup" "$target"
            rm -rf "$staged"
            open "$target"
        fi
    else
        rm -rf "$staged"
        open "$target"
    fi
    rm -rf "$work"
    """
}
