import Foundation

/// Locates the Antigravity app (bundle id `com.google.antigravity`). The bundle is used to
/// confirm a running `language_server` really belongs to Antigravity rather than an unrelated
/// process sharing the name, to launch the engine directly when the app is closed, and to
/// read the IDE version the engine is told it runs under.
///
/// GaugeCore is Foundation-only (no NSWorkspace), so the bundle is looked for where macOS
/// installs apps: `/Applications`, then `~/Applications`.
struct AntigravityInstall: Sendable, Equatable {
    /// The `Antigravity.app` bundle directory, symlinks resolved.
    let bundle: URL

    static let bundleIdentifier = "com.google.antigravity"
    static let bundleName = "Antigravity.app"

    static func defaultSearchRoots() -> [URL] {
        [
            URL(fileURLWithPath: "/Applications", isDirectory: true),
            FileManager.default.homeDirectoryForCurrentUser.appending(path: "Applications", directoryHint: .isDirectory),
        ]
    }

    /// The first `Antigravity.app` under the roots, or nil when Antigravity is not installed.
    /// The bundle id is checked when readable so a renamed unrelated app is not mistaken for it.
    static func locate(in roots: [URL] = defaultSearchRoots()) -> AntigravityInstall? {
        for root in roots {
            let bundle = root.appending(path: bundleName, directoryHint: .isDirectory).resolvingSymlinksInPath()
            var isDirectory: ObjCBool = false
            guard FileManager.default.fileExists(atPath: bundle.path, isDirectory: &isDirectory), isDirectory.boolValue else {
                continue
            }
            if let identifier = infoPlist(bundle)?["CFBundleIdentifier"] as? String, identifier != bundleIdentifier {
                continue
            }
            return AntigravityInstall(bundle: bundle)
        }
        return nil
    }

    /// The standalone language server launched in delegate mode.
    var engineExecutable: URL {
        bundle.appending(path: "Contents/Resources/bin/language_server", directoryHint: .notDirectory)
    }

    var hasEngine: Bool { FileManager.default.isExecutableFile(atPath: engineExecutable.path) }

    /// The value for `--override_ide_version`: the app's `CFBundleShortVersionString`, which is
    /// what the app passes (`app.getVersion()`). Read per launch so it tracks updates. Nil when
    /// unreadable — the override is then omitted rather than sent with a bogus value.
    var ideVersion: String? {
        ProviderText.normalize(Self.infoPlist(bundle)?["CFBundleShortVersionString"] as? String)
    }

    /// Whether an executable path lies inside this bundle. Both sides are symlink-resolved and
    /// compared by path components, so `/Applications/Antigravity.app.bak/…` does not match.
    func contains(_ executablePath: String) -> Bool {
        let root = bundle.resolvingSymlinksInPath().standardizedFileURL.pathComponents
        let path = URL(fileURLWithPath: executablePath).resolvingSymlinksInPath().standardizedFileURL.pathComponents
        return path.count > root.count && Array(path.prefix(root.count)) == root
    }

    private static func infoPlist(_ bundle: URL) -> [String: Any]? {
        let url = bundle.appending(path: "Contents/Info.plist", directoryHint: .notDirectory)
        guard let data = try? Data(contentsOf: url) else { return nil }
        return (try? PropertyListSerialization.propertyList(from: data, format: nil)) as? [String: Any]
    }
}
