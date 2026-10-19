import AppKit
import CoreText
import GaugeCore
import SwiftUI

@main
struct AgentGaugeApp: App {
    @State private var model: AppModel
    @State private var update: UpdateModel

    init() {
        // The language is fixed for the process lifetime. Bundle.preferredLocalizations
        // honours the per-app language from System Settings, but only when the bundle
        // actually declares ko/en/ja (an unbundled `swift run` reports just "en").
        let bundleLanguages = Bundle.main.localizations.filter { $0 != "Base" }
        let preferred = bundleLanguages.count > 1 ? Bundle.main.preferredLocalizations : Locale.preferredLanguages
        L10n.configure(L10n.resolve(preferred: preferred))
        let model = AppModel()
        _model = State(initialValue: model)
        let update = UpdateModel()
        _update = State(initialValue: update)
        update.checkInBackground()
        // The updater relaunches with --updated; say that it landed, since a menu bar app has
        // no window that would otherwise show the new version.
        if CommandLine.arguments.contains("--updated") {
            model.announceUpdate(to: update.currentVersion)
        }
        if let directory = PreviewRenderer.directoryArgument() {
            PreviewRenderer.run(model: model, update: update, into: directory)
        }
    }

    var body: some Scene {
        MenuBarExtra {
            PopoverView()
                .environment(model)
                .environment(update)
        } label: {
            MenuBarLabel(summary: model.menuBarSummary, basis: model.displayBasis, showPercent: model.showPercentInMenuBar)
        }
        .menuBarExtraStyle(.window)

        Settings {
            SettingsView()
                .environment(model)
                .environment(update)
        }
        .windowResizability(.contentMinSize)
    }
}

/// The menu bar item: the Windows tray gauge, whose needle follows the highest usage level,
/// optionally followed by that window's percentage. It is drawn as a template (monochrome,
/// tinted by the menu bar) rather than in the Windows tray's level colours.
///
/// The two are drawn into one template image rather than handed to the status item as an
/// Image + Text pair: the status button lays a title out on its own line box, which leaves
/// the digits about a point above the icon's center. Drawing them ourselves puts the
/// digits' cap-height center on the icon's center, and the menu bar still tints the
/// template like its own text.
struct MenuBarLabel: View {
    let summary: MenuBarSummary
    let basis: DisplayBasis
    let showPercent: Bool

    var body: some View {
        let percent = showPercent ? summary.percentText(basis: basis) : nil
        Image(nsImage: Self.render(icon: Self.icon(for: summary), text: percent))
            .accessibilityLabel("AgentGauge")
            .accessibilityValue(summary.percentText(basis: basis) ?? "")
    }

    private static let spacing: CGFloat = 5

    /// Optical nudge, tuned by eye on a Retina menu bar: the gauge opens at the bottom, so its
    /// bounding-box center sits above where it reads; one device pixel down settles it
    /// against the digits.
    private static let iconDrop: CGFloat = 0.5

    /// Point size of the tray gauge; its artwork fills the width and ~85% of the height,
    /// which reads about as large as the SF Symbol it replaces.
    private static let iconSize = NSSize(width: 16, height: 16)

    private static let trayIcons: [String: NSImage] = Dictionary(uniqueKeysWithValues:
        ["gauge_icon", "gauge_icon_70", "gauge_icon_90"].compactMap { name in
            Bundle.main.url(forResource: name, withExtension: "ico")
                .flatMap(NSImage.init(contentsOf:))
                .map { (name, $0) }
        })

    /// Same asset choice as the Windows tray (`TrayIconService`): plain, `_70`, `_90`.
    /// An unbundled `swift run` has no resources, so it falls back to the SF Symbol.
    private static func icon(for summary: MenuBarSummary) -> (image: NSImage, size: NSSize) {
        let name = switch summary.level {
        case .danger: "gauge_icon_90"
        case .caution: "gauge_icon_70"
        case .ok, nil: "gauge_icon"
        }
        if let icon = trayIcons[name] { return (icon, iconSize) }
        let symbol = NSImage(systemSymbolName: summary.symbolName, accessibilityDescription: nil)?
            .withSymbolConfiguration(.init(pointSize: NSFont.menuBarFont(ofSize: 0).pointSize, weight: .regular)) ?? NSImage()
        return (symbol, symbol.size)
    }

    private static func render(icon: (image: NSImage, size: NSSize), text: String?) -> NSImage {
        let font = NSFont.menuBarFont(ofSize: 0)
        let iconSize = icon.size
        let line = text.map {
            CTLineCreateWithAttributedString(NSAttributedString(string: $0, attributes: [.font: monospacedDigits(font)]))
        }
        let textWidth = line.map { CGFloat(CTLineGetTypographicBounds($0, nil, nil, nil)) } ?? 0
        let gap = line == nil ? 0 : spacing
        let size = NSSize(width: ceil(iconSize.width + gap + textWidth), height: iconSize.height)
        let image = NSImage(size: size, flipped: false) { _ in
            icon.image.draw(in: NSRect(origin: CGPoint(x: 0, y: -iconDrop), size: iconSize))
            if let line, let context = NSGraphicsContext.current?.cgContext {
                // Lining digits and "%" span baseline to cap height, so centering that span
                // on the image (the gauge's own center) aligns them optically.
                context.textPosition = CGPoint(x: iconSize.width + gap, y: (size.height - font.capHeight) / 2)
                CTLineDraw(line, context)
            }
            return true
        }
        image.isTemplate = true
        return image
    }

    /// Tabular figures keep the item from shifting width as the percentage changes.
    private static func monospacedDigits(_ font: NSFont) -> NSFont {
        let descriptor = font.fontDescriptor.addingAttributes([
            .featureSettings: [[
                NSFontDescriptor.FeatureKey.typeIdentifier: kNumberSpacingType,
                NSFontDescriptor.FeatureKey.selectorIdentifier: kMonospacedNumbersSelector,
            ]],
        ])
        return NSFont(descriptor: descriptor, size: font.pointSize) ?? font
    }
}
