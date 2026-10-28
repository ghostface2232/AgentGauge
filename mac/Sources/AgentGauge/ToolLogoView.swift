import SwiftUI
import GaugeCore

/// The tool's logo from the same `ToolLogos` SVGs the menu bar ring and the Windows app use,
/// filled with the foreground style so it follows the appearance.
enum ToolLogos {
    /// Logo files share their names with the Windows `ToolKind` members.
    static let all: [ToolKind: SVGLogo] = Dictionary(uniqueKeysWithValues:
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
}

private struct LogoShape: Shape {
    let logo: SVGLogo

    func path(in rect: CGRect) -> Path {
        // Fit the logo's box inside the rect, centred, keeping its aspect ratio.
        let scale = min(rect.width / logo.width, rect.height / logo.height)
        let transform = CGAffineTransform(
            translationX: rect.midX - logo.width * scale / 2,
            y: rect.midY - logo.height * scale / 2)
            .scaledBy(x: scale, y: scale)
        var path = Path()
        for figure in logo.figures {
            path.move(to: CGPoint(x: figure.start.x, y: figure.start.y).applying(transform))
            for segment in figure.segments {
                path.addCurve(
                    to: CGPoint(x: segment.end.x, y: segment.end.y).applying(transform),
                    control1: CGPoint(x: segment.control1.x, y: segment.control1.y).applying(transform),
                    control2: CGPoint(x: segment.control2.x, y: segment.control2.y).applying(transform))
            }
            if figure.closed { path.closeSubpath() }
        }
        return path
    }
}

struct ToolLogoView: View {
    let tool: ToolKind

    var body: some View {
        if let logo = ToolLogos.all[tool] {
            LogoShape(logo: logo)
                .fill(Color(nsColor: .labelColor).opacity(0.75), style: FillStyle(eoFill: logo.evenOdd))
                .accessibilityHidden(true)
        }
    }
}
