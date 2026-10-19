import AppKit
import GaugeCore
import SwiftUI

/// Liquid Glass is applied the way Apple's own menu bar extras use it: the panel itself
/// (provided by the system on macOS 26) and the controls floating on it are glass; the
/// content — the usage cards — stays a quiet tinted fill, so glass is never stacked on glass.
/// Older systems fall back to the standard borderless/bordered controls.
enum Metrics {
    static let panelWidth: CGFloat = 340
    /// The fixed radius before macOS 26; from 26 the minimum of the concentric card shape.
    static let cardCornerRadius: CGFloat = {
        if #available(macOS 26.0, *) { return 14 }
        return 12
    }()
    static let cardPadding: CGFloat = 14
}

extension View {
    /// A round icon button: glass on macOS 26, borderless before.
    @ViewBuilder
    func iconButtonStyle() -> some View {
        #if compiler(>=6.2)
        if #available(macOS 26.0, *) {
            buttonStyle(.glass).buttonBorderShape(.circle)
        } else {
            buttonStyle(.borderless)
        }
        #else
        buttonStyle(.borderless)
        #endif
    }

    /// A small text action inside a card.
    @ViewBuilder
    func cardActionStyle() -> some View {
        #if compiler(>=6.2)
        if #available(macOS 26.0, *) {
            buttonStyle(.glass).controlSize(.small)
        } else {
            buttonStyle(.bordered).controlSize(.small)
        }
        #else
        buttonStyle(.bordered).controlSize(.small)
        #endif
    }

    /// On macOS 26 the card shape is concentric with the panel window, as system panels nest
    /// their modules; a fixed radius reads rounder than the window around it.
    @ViewBuilder
    func cardBackground() -> some View {
        #if compiler(>=6.2)
        if #available(macOS 26.0, *) {
            background(.quaternary, in: ConcentricRectangle(
                corners: .concentric(minimum: .fixed(Metrics.cardCornerRadius)), isUniform: true))
        } else {
            background(.quaternary, in: RoundedRectangle(cornerRadius: Metrics.cardCornerRadius, style: .continuous))
        }
        #else
        background(.quaternary, in: RoundedRectangle(cornerRadius: Metrics.cardCornerRadius, style: .continuous))
        #endif
    }
}

extension UsageLevel {
    /// The bar tint. Healthy usage follows the user's accent colour like other system meters.
    var tint: Color {
        switch self {
        case .ok: .accentColor
        case .caution: .orange
        case .danger: .red
        }
    }
}

/// Calls back whenever the hosting window becomes key — i.e. each time the menu bar panel
/// opens. `onAppear` alone is not reliable for that: the panel's view can stay alive
/// between openings.
struct WindowKeyObserver: NSViewRepresentable {
    let onBecomeKey: @MainActor () -> Void

    func makeNSView(context: Context) -> ObserverView {
        let view = ObserverView()
        view.onBecomeKey = onBecomeKey
        return view
    }

    func updateNSView(_ view: ObserverView, context: Context) {
        view.onBecomeKey = onBecomeKey
    }

    final class ObserverView: NSView {
        var onBecomeKey: (@MainActor () -> Void)?
        private var token: NSObjectProtocol?

        override func viewDidMoveToWindow() {
            super.viewDidMoveToWindow()
            if let token { NotificationCenter.default.removeObserver(token) }
            token = nil
            guard let window else { return }
            token = NotificationCenter.default.addObserver(
                forName: NSWindow.didBecomeKeyNotification, object: window, queue: .main
            ) { [weak self] _ in
                MainActor.assumeIsolated { self?.onBecomeKey?() }
            }
        }

        deinit {
            if let token { NotificationCenter.default.removeObserver(token) }
        }
    }
}
