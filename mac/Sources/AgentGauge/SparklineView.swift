import GaugeCore
import SwiftUI

/// The bar row's remaining-quota sparkline: the last six hours of readings in the current
/// cycle, with a dashed line for what an even spend would have left. Hovering shows the
/// nearest reading in place of the reset caption; leaving restores it.
struct UsageSparkline: View {
    let burndown: UsageBurndown
    @Binding var hoverText: String?
    @State private var hoverX: Double?

    var body: some View {
        GeometryReader { proxy in
            let size = proxy.size
            ZStack {
                if burndown.hasIdealLine {
                    line(size) { $0.idealRemaining ?? 0 }
                        .stroke(.tertiary, style: StrokeStyle(lineWidth: 1, lineCap: .round, dash: [2, 2]))
                }
                line(size) { $0.remaining }
                    .stroke(.secondary, style: StrokeStyle(lineWidth: 1.25, lineCap: .round, lineJoin: .round))
                if let hoverX, let point = burndown.nearest(toX: hoverX) {
                    Circle()
                        .fill(.primary)
                        .frame(width: 4, height: 4)
                        .position(position(point, in: size))
                }
            }
            .contentShape(Rectangle())
            .onContinuousHover { phase in
                switch phase {
                case .active(let location):
                    let x = size.width > 0 ? Double(location.x / size.width) : 0
                    hoverX = x
                    hoverText = burndown.hoverText(atX: x)
                case .ended:
                    hoverX = nil
                    hoverText = nil
                }
            }
        }
        .onDisappear { hoverText = nil }
        .accessibilityHidden(true)
    }

    /// Strokes are inset by half a point so the line never clips at the frame's edges.
    private func position(_ point: UsageBurndown.Point, in size: CGSize, value: Double? = nil) -> CGPoint {
        let inset: CGFloat = 1
        let y = min(max(value ?? point.remaining, 0), 1)
        return CGPoint(x: inset + CGFloat(point.x) * (size.width - inset * 2),
                       y: inset + CGFloat(1 - y) * (size.height - inset * 2))
    }

    private func line(_ size: CGSize, value: @escaping (UsageBurndown.Point) -> Double) -> Path {
        Path { path in
            for (index, point) in burndown.points.enumerated() {
                let location = position(point, in: size, value: value(point))
                if index == 0 { path.move(to: location) } else { path.addLine(to: location) }
            }
        }
    }
}
