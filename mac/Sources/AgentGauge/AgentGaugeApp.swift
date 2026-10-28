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
                .background { if SettingsDemo.isRequested { SettingsDemoDriver() } }
        }
        .menuBarExtraStyle(.window)

        Settings {
            SettingsView()
                .environment(model)
                .environment(update)
        }
        .windowResizability(.contentSize)
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
        Image(nsImage: Self.image(summary: summary, basis: basis, text: percent))
            .accessibilityLabel("AgentGauge")
            .accessibilityValue(summary.percentText(basis: basis) ?? "")
    }

    private static let spacing: CGFloat = 5

    /// Window tags ("5H", "W") are dimmed against their digits; template alpha, so the menu
    /// bar still tints them. The two arcs' readings are set apart by the same gap as the icon from its text.
    private static let labelAlpha: CGFloat = 0.55

    private static func attributed(_ text: String, font: NSFont) -> NSAttributedString {
        let result = NSMutableAttributedString()
        let plain: [NSAttributedString.Key: Any] = [.font: font]
        let dim = plain.merging([.foregroundColor: NSColor.black.withAlphaComponent(labelAlpha)]) { $1 }
        for (index, part) in text.components(separatedBy: " | ").enumerated() {
            if index > 0 {
                // One space whose advance is stretched to `spacing`, so the gap matches the icon's.
                let space = NSAttributedString(string: " ", attributes: [.font: font])
                let natural = CGFloat(CTLineGetTypographicBounds(CTLineCreateWithAttributedString(space), nil, nil, nil))
                result.append(NSAttributedString(string: " ", attributes: [.font: font, .kern: spacing - natural]))
            }
            if let space = part.firstIndex(of: " ") {
                result.append(NSAttributedString(string: String(part[...space]), attributes: dim))
                result.append(NSAttributedString(string: String(part[part.index(after: space)...]), attributes: plain))
            } else {
                result.append(NSAttributedString(string: part, attributes: plain))
            }
        }
        return result
    }

    /// Optical nudge, tuned by eye on a Retina menu bar: the gauge opens at the bottom, so its
    /// bounding-box center sits above where it reads; one device pixel down settles it
    /// against the digits.
    private static let iconDrop: CGFloat = 0.5

    /// Point size of the tray gauge; its artwork fills the width and ~85% of the height,
    /// which reads about as large as the SF Symbol it replaces.
    private static let iconSize = NSSize(width: 16, height: 16)

    /// The ring carries a logo and two arcs, so at the gauge's 16pt it reads smaller than the
    /// system's own status items (~20pt).
    private static let ringSize = NSSize(width: 20, height: 20)

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

    /// The chosen tool's ring when there is one, otherwise the gauge. The ring fills its box
    /// symmetrically, so it needs none of the gauge's optical drop.
    private static func image(summary: MenuBarSummary, basis: DisplayBasis, text: String?) -> NSImage {
        if let ring = summary.ring {
            return render(icon: (ringImage(ring, basis: basis), ringSize), drop: 0, text: text)
        }
        return render(icon: icon(for: summary), drop: iconDrop, text: text)
    }

    private static func render(icon: (image: NSImage, size: NSSize), drop: CGFloat, text: String?) -> NSImage {
        let font = NSFont.menuBarFont(ofSize: 0)
        let iconSize = icon.size
        let line = text.map { CTLineCreateWithAttributedString(attributed($0, font: monospacedDigits(font))) }
        let textWidth = line.map { CGFloat(CTLineGetTypographicBounds($0, nil, nil, nil)) } ?? 0
        let gap = line == nil ? 0 : spacing
        let size = NSSize(width: ceil(iconSize.width + gap + textWidth), height: iconSize.height)
        let image = NSImage(size: size, flipped: false) { _ in
            icon.image.draw(in: NSRect(origin: CGPoint(x: 0, y: -drop), size: iconSize))
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

    // The chosen tool's ring, the Windows tray ring (`TrayRingIconRenderer`) in template form:
    // a monochrome image whose alpha carries the track, the arcs and the logo, so the menu
    // bar tints it like its own items. The level colours of the Windows ring are therefore
    // absent here, as they are for the gauge.
    private enum Ring {
        static let strokeShare: CGFloat = 0.095
        static let minStroke: CGFloat = 1.25
        static let gapDegrees: CGFloat = 28
        static let trackAlpha: CGFloat = 0.3
        static let logoPaddingShare: CGFloat = 0.06
        static let logoSpan: CGFloat = 1.7
        // Antigravity's arch has wide feet that read as touching the bottom arc when it is
        // centred by its bounding box.
        static let antigravityLiftShare: CGFloat = 0.03
    }

    /// Logo files share their names with the Windows `ToolKind` members.
    private static let logos: [ToolKind: SVGLogo] = Dictionary(uniqueKeysWithValues:
        ToolKind.allCases.compactMap { tool in
            let name = switch tool {
            case .claude: "ClaudeCode"
            case .codex: "Codex"
            case .cursor: "Cursor"
            case .antigravity: "Antigravity"
            case .githubCopilot: "GitHubCopilot"
            }
            return Bundle.main.url(forResource: name, withExtension: "svg", subdirectory: "ToolLogos")
                .flatMap { try? String(contentsOf: $0, encoding: .utf8) }
                .flatMap(SVGLogo.init(svg:))
                .map { (tool, $0) }
        })

    private static func ringImage(_ ring: StatusRing, basis: DisplayBasis) -> NSImage {
        let side = ringSize.width
        let image = NSImage(size: ringSize, flipped: false) { _ in
            guard let context = NSGraphicsContext.current?.cgContext else { return false }
            let stroke = max(Ring.minStroke, side * Ring.strokeShare)
            let radius = side / 2 - stroke / 2
            let center = CGPoint(x: side / 2, y: side / 2)
            let span = 180 - Ring.gapDegrees
            context.setLineWidth(stroke)
            context.setLineCap(.round)

            // y points up: the top arc runs clockwise from 9 o'clock over the top, the bottom
            // arc counter-clockwise under it, so both fill from the left.
            func arc(start: CGFloat, sweep: CGFloat, alpha: CGFloat) {
                let path = CGMutablePath()
                path.addArc(center: center, radius: radius,
                            startAngle: start * .pi / 180, endAngle: (start + sweep) * .pi / 180,
                            clockwise: sweep < 0)
                context.addPath(path)
                context.setStrokeColor(NSColor.black.withAlphaComponent(alpha).cgColor)
                context.strokePath()
            }
            for (window, start, direction) in [(ring.short, 180 - Ring.gapDegrees / 2, CGFloat(-1)),
                                               (ring.long, 180 + Ring.gapDegrees / 2, CGFloat(1))] {
                arc(start: start, sweep: direction * span, alpha: Ring.trackAlpha)
                if let window, window.usedRatio.isFinite {
                    let fill = CGFloat(basis.fraction(for: window.usedRatio))
                    if fill > 0 { arc(start: start, sweep: direction * span * fill, alpha: 1) }
                }
            }

            let inner = radius - stroke / 2 - max(1, side * Ring.logoPaddingShare)
            if let logo = logos[ring.tool], inner > 2 {
                let scale = inner * Ring.logoSpan / CGFloat(max(logo.width, logo.height))
                let lift = ring.tool == .antigravity ? side * Ring.antigravityLiftShare : 0
                // SVG y points down; flip it while centring the logo.
                let transform = CGAffineTransform(translationX: center.x, y: center.y + lift)
                    .scaledBy(x: scale, y: -scale)
                    .translatedBy(x: -CGFloat(logo.width) / 2, y: -CGFloat(logo.height) / 2)
                let path = CGMutablePath()
                for figure in logo.figures {
                    path.move(to: CGPoint(x: figure.start.x, y: figure.start.y), transform: transform)
                    for segment in figure.segments {
                        path.addCurve(to: CGPoint(x: segment.end.x, y: segment.end.y),
                                      control1: CGPoint(x: segment.control1.x, y: segment.control1.y),
                                      control2: CGPoint(x: segment.control2.x, y: segment.control2.y),
                                      transform: transform)
                    }
                    if figure.closed { path.closeSubpath() }
                }
                context.addPath(path)
                context.setFillColor(NSColor.black.cgColor)
                context.fillPath(using: logo.evenOdd ? .evenOdd : .winding)
            }
            return true
        }
        image.isTemplate = true
        return image
    }

    /// Tabular figures keep the item from shifting width as the percentage changes.
    private static func monospacedDigits(_ font: NSFont) -> NSFont {
        let descriptor = (font.fontDescriptor.withDesign(.rounded) ?? font.fontDescriptor).addingAttributes([
            .featureSettings: [[
                NSFontDescriptor.FeatureKey.typeIdentifier: kNumberSpacingType,
                NSFontDescriptor.FeatureKey.selectorIdentifier: kMonospacedNumbersSelector,
            ]],
        ])
        return NSFont(descriptor: descriptor, size: font.pointSize) ?? font
    }
}
