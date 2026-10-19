import GaugeCore
import SwiftUI

@main
struct AgentGaugeApp: App {
    @State private var model: AppModel

    init() {
        // The language is fixed for the process lifetime. Bundle.preferredLocalizations
        // honours the per-app language from System Settings, but only when the bundle
        // actually declares ko/en/ja (an unbundled `swift run` reports just "en").
        let bundleLanguages = Bundle.main.localizations.filter { $0 != "Base" }
        let preferred = bundleLanguages.count > 1 ? Bundle.main.preferredLocalizations : Locale.preferredLanguages
        L10n.configure(L10n.resolve(preferred: preferred))
        _model = State(initialValue: AppModel())
    }

    var body: some Scene {
        MenuBarExtra {
            PopoverView()
                .environment(model)
        } label: {
            MenuBarLabel(summary: model.menuBarSummary, basis: model.displayBasis, showPercent: model.showPercentInMenuBar)
        }
        .menuBarExtraStyle(.window)

        Settings {
            SettingsView()
                .environment(model)
        }
        .windowResizability(.contentSize)
    }
}

/// The menu bar item: a template gauge symbol whose needle follows the highest usage level,
/// optionally followed by that window's percentage.
struct MenuBarLabel: View {
    let summary: MenuBarSummary
    let basis: DisplayBasis
    let showPercent: Bool

    var body: some View {
        HStack(spacing: 4) {
            Image(systemName: summary.symbolName)
            if showPercent, let percent = summary.percentText(basis: basis) {
                Text(percent).monospacedDigit()
            }
        }
        .accessibilityElement(children: .combine)
        .accessibilityLabel("AgentGauge")
        .accessibilityValue(summary.percentText(basis: basis) ?? "")
    }
}
