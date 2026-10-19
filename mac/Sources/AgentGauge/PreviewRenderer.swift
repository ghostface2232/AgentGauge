import AppKit
import GaugeCore
import SwiftUI

/// Developer visual QA, the Mac counterpart of the Windows `--popover-demo` switch:
/// `AgentGauge --render-preview <dir>` runs the app normally for ~15 s (its menu bar item
/// shows, and every shown provider is read, so a second instance spends one more of Claude's
/// few reads), then draws the panel's cards offscreen in each view mode and appearance to PNGs
/// in `<dir>` and quits. That instance records no history, posts no alerts, skips the update
/// check and never persists the modes it draws; the images hold the usage the panel shows.
@MainActor
enum PreviewRenderer {
    static func directoryArgument() -> URL? {
        let arguments = CommandLine.arguments
        guard let index = arguments.firstIndex(of: "--render-preview"), index + 1 < arguments.count else { return nil }
        return URL(fileURLWithPath: arguments[index + 1], isDirectory: true)
    }

    static func run(model: AppModel, update: UpdateModel, into directory: URL) {
        Task { @MainActor in
            // Long enough for every shown provider's first read (Antigravity's delegate
            // engine takes several seconds).
            try? await Task.sleep(for: .seconds(15))
            try? FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
            for mode in UsageViewMode.allCases {
                model.viewModeOverride = mode
                for scheme in [ColorScheme.light, .dark] {
                    let content = PreviewPanel()
                        .environment(model)
                        .environment(update)
                        .environment(\.colorScheme, scheme)
                    let renderer = ImageRenderer(content: content)
                    renderer.scale = 2
                    guard let image = renderer.nsImage, let tiff = image.tiffRepresentation,
                          let png = NSBitmapImageRep(data: tiff)?.representation(using: .png, properties: [:]) else { continue }
                    let name = "panel-\(mode.rawValue)-\(scheme == .dark ? "dark" : "light").png"
                    try? png.write(to: directory.appending(path: name))
                }
            }
            model.viewModeOverride = nil
            NSApp.terminate(nil)
        }
    }
}

/// The panel's cards without the scroll view (which an offscreen renderer cannot draw), on
/// an opaque window-coloured background standing in for the glass panel.
private struct PreviewPanel: View {
    @Environment(AppModel.self) private var model
    @Environment(\.colorScheme) private var scheme

    var body: some View {
        VStack(spacing: 10) {
            ForEach(model.states) { state in
                ToolCard(state: state, onSignIn: {}, onRetry: {})
            }
        }
        .padding(12)
        .frame(width: Metrics.panelWidth)
        .background(scheme == .dark ? Color(white: 0.16) : Color(white: 0.94))
    }
}
