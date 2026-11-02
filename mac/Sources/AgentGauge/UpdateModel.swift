import AppKit
import GaugeCore
import Observation

/// The update control in Settings (and the panel footer's prompt): one action that checks
/// GitHub Releases and, once a newer build is found, applies it. The launch check is quiet and
/// installs by itself (except on the relaunch after an update); the button is the manual path.
@MainActor
@Observable
final class UpdateModel {
    enum Phase: Equatable {
        case idle
        case checking
        case upToDate
        case available(AppRelease)
        case checkFailed
        case downloading
        case installing
        case installFailed(notWritable: Bool)
    }

    private(set) var phase: Phase = .idle
    let currentVersion: String
    @ObservationIgnored private let service: UpdateService?

    init() {
        let version = Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? ""
        currentVersion = version.isEmpty ? "—" : version
        // An unbundled `swift run` has no version or bundle to replace, so it never updates.
        if let parsed = AppVersion(version), let identifier = Bundle.main.bundleIdentifier,
           Bundle.main.bundleURL.pathExtension == "app" {
            service = UpdateService(currentVersion: parsed, bundleIdentifier: identifier)
        } else {
            service = nil
        }
    }

    var isAvailable: Bool {
        if case .available = phase { return true }
        return false
    }

    /// A quiet check leaves `phase` alone (it says nothing unless it finds something), so it is
    /// tracked separately; without it a click or the auto-update switch would overlap it.
    private(set) var isQuietChecking = false
    @ObservationIgnored private var installWhenFound = false

    var isBusy: Bool { isQuietChecking || [.checking, .downloading, .installing].contains(phase) }

    /// A failed install started from the panel says why there, too, rather than the prompt
    /// simply vanishing; clicking it checks again.
    var hasFailedInstall: Bool {
        if case .installFailed = phase { return true }
        return false
    }

    var actionTitle: String { L10n.get(isAvailable ? "Update_Apply" : "Update_Check") }

    var statusText: String? {
        switch phase {
        case .idle: nil
        case .checking: L10n.get("Update_Checking")
        case .upToDate: L10n.get("Update_UpToDate")
        case .available(let release): L10n.format("Update_AvailableVersion", release.version.description)
        case .checkFailed: L10n.get("Update_CheckFailed")
        case .downloading: L10n.get("Update_Downloading")
        case .installing: L10n.get("Update_Installing")
        case .installFailed(let notWritable): L10n.get(notWritable ? "Update_NotWritable" : "Update_InstallFailed")
        }
    }

    /// The quiet launch check: says nothing unless an update is found. With `installIfAvailable`
    /// a found update is installed at once, which is how the first launch after login updates
    /// itself.
    func checkInBackground(installIfAvailable: Bool = false) {
        guard phase == .idle, !isQuietChecking, PreviewRenderer.directoryArgument() == nil else { return }
        installWhenFound = installIfAvailable
        runQuietCheck()
    }

    private func runQuietCheck() {
        guard let service else { return }
        isQuietChecking = true
        Task {
            let result = await service.check()
            isQuietChecking = false
            let install = installWhenFound
            installWhenFound = false
            guard case .available(let release) = result else { return }
            phase = .available(release)
            if install { self.install(release) }
        }
    }

    /// Turning automatic installs on applies now: check, and install whatever is found.
    func checkAndInstall() {
        guard service != nil, PreviewRenderer.directoryArgument() == nil else { return }
        // A check already running (quiet or a manual click) will install what it finds.
        if isQuietChecking || phase == .checking { installWhenFound = true; return }
        guard !isBusy else { return }
        if case .available(let release) = phase {
            install(release)
            return
        }
        installWhenFound = true
        runQuietCheck()
    }

    func performAction() {
        guard !isBusy else { return }
        if case .available(let release) = phase {
            install(release)
        } else {
            check()
        }
    }

    private func check() {
        guard let service else {
            phase = .checkFailed
            return
        }
        phase = .checking
        Task {
            switch await service.check() {
            case .available(let release):
                phase = .available(release)
                if installWhenFound { installWhenFound = false; self.install(release) }
            case .upToDate: phase = .upToDate
            case .failed: phase = .checkFailed
            }
            installWhenFound = false
        }
    }

    private func install(_ release: AppRelease) {
        guard let service else { return }
        phase = .downloading
        Task {
            do {
                try await service.install(release, replacing: Bundle.main.bundleURL,
                                          processID: ProcessInfo.processInfo.processIdentifier)
                phase = .installing
                // The helper swaps the bundle once this process is gone, then relaunches it.
                try? await Task.sleep(for: .milliseconds(300))
                NSApp.terminate(nil)
            } catch {
                // Back to "Check for Updates", so a retry re-reads the release (and a re-uploaded
                // draft asset's new digest) instead of failing against the old one.
                phase = .installFailed(notWritable: (error as? UpdateInstallError) == .notWritable)
            }
        }
    }
}
