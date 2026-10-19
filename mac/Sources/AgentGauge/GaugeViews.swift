import GaugeCore
import SwiftUI

/// A 270° ring that opens at the bottom, like a speedometer: the track is the full arc and
/// the fill sweeps clockwise from the lower left by the shown fraction.
struct UsageGaugeRing: View {
    let fraction: Double
    let level: UsageLevel
    var lineWidth: CGFloat = 7

    private static let sweep = 0.75

    var body: some View {
        let clamped = min(max(fraction, 0), 1)
        ZStack {
            arc(Self.sweep)
                .stroke(.quaternary, style: StrokeStyle(lineWidth: lineWidth, lineCap: .round))
            if clamped > 0 {
                arc(Self.sweep * clamped)
                    .stroke(level.tint, style: StrokeStyle(lineWidth: lineWidth, lineCap: .round))
            }
        }
        // Start at 135° (lower left) so the 90° gap is centred at the bottom.
        .rotationEffect(.degrees(135))
        .padding(lineWidth / 2)
        .animation(.smooth(duration: 0.45), value: clamped)
        .accessibilityHidden(true)
    }

    private func arc(_ length: Double) -> some Shape {
        Circle().trim(from: 0, to: length)
    }
}

/// One window in gauge mode: label above, ring with the number stacked in its centre, and the
/// reset/pace/ETA captions below.
struct GaugeTile: View {
    let window: UsageWindow
    let details: RowDetails
    let basis: DisplayBasis
    let showsGroup: Bool

    var body: some View {
        let percent = basis.percent(for: window.usedRatio)
        VStack(spacing: 4) {
            VStack(spacing: 1) {
                if showsGroup, let group = window.groupLabel {
                    Text(verbatim: group)
                        .font(.caption.weight(.semibold))
                        .lineLimit(1)
                }
                Text(WindowLabels.label(for: window))
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .lineLimit(1)
            }
            ZStack {
                UsageGaugeRing(fraction: basis.fraction(for: window.usedRatio), level: UsageLevel(ratio: window.usedRatio))
                VStack(spacing: -2) {
                    Text(verbatim: "\(percent)")
                        .font(.system(size: 22, weight: .semibold, design: .rounded))
                        .monospacedDigit()
                        .contentTransition(.numericText(value: Double(percent)))
                    Text(verbatim: "%")
                        .font(.caption2)
                        .foregroundStyle(.secondary)
                }
                .offset(y: 2)
            }
            .frame(width: 76, height: 76)
            VStack(spacing: 1) {
                if !details.reset.isEmpty { Text(details.reset) }
                if let pace = details.pace { Text(pace).help(L10n.get("Pace_Tooltip")) }
                if let eta = details.eta { Text(eta) }
            }
            .font(.caption2)
            .foregroundStyle(.secondary)
            .multilineTextAlignment(.center)
            .fixedSize(horizontal: false, vertical: true)
        }
        .frame(maxWidth: .infinity)
        .animation(.smooth(duration: 0.35), value: percent)
        .accessibilityElement(children: .ignore)
        .accessibilityLabel(details.accessibilityText(window: window, basis: basis))
    }
}

/// Gauge mode for one card: each model family is its own row of gauges, divided like bar mode.
struct GaugeGroupsView: View {
    let groups: [GaugeCore.WindowGroup]
    let basis: DisplayBasis
    let details: (UsageWindow) -> RowDetails

    /// Even columns sized to the card's largest group (two or three), shared by every group so
    /// gauges line up across the dividers and a row of two spreads over the full width.
    private var columns: [GridItem] {
        let count = min(max(groups.map(\.windows.count).max() ?? 1, 2), 3)
        return Array(repeating: GridItem(.flexible(), spacing: 8, alignment: .top), count: count)
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            ForEach(Array(groups.enumerated()), id: \.element.id) { index, group in
                if index > 0 { Divider() }
                LazyVGrid(columns: columns, alignment: .center, spacing: 10) {
                    ForEach(group.windows) { window in
                        GaugeTile(window: window, details: details(window), basis: basis, showsGroup: group.label != nil)
                    }
                }
            }
        }
    }
}
