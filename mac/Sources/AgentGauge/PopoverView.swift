import AppKit
import GaugeCore
import SwiftUI

/// The menu bar panel: a title row with refresh and settings, one card per shown tool, and
/// a quiet footer. It sizes to its cards and scrolls only past most of the screen height.
struct PopoverView: View {
    @Environment(AppModel.self) private var model
    @Environment(\.openSettings) private var openSettings
    @State private var contentHeight: CGFloat = 0

    var body: some View {
        VStack(spacing: 0) {
            header
                .padding(.horizontal, 16)
                .padding(.top, 14)
                .padding(.bottom, 10)

            ScrollView(.vertical) {
                cards
                    .padding(.horizontal, 12)
                    .onGeometryChange(for: CGFloat.self) { proxy in
                        proxy.size.height
                    } action: { height in
                        contentHeight = height
                    }
            }
            .scrollBounceBehavior(.basedOnSize)
            .frame(height: min(max(contentHeight, 44), maximumScrollHeight))

            footer
                .padding(.horizontal, 16)
                .padding(.top, 10)
                .padding(.bottom, 12)
        }
        .frame(width: Metrics.panelWidth)
        .background(WindowKeyObserver { model.popoverOpened() })
    }

    private var maximumScrollHeight: CGFloat {
        let screen = NSScreen.main?.visibleFrame.height ?? 800
        return min(screen - 160, 640)
    }

    private var header: some View {
        HStack(spacing: 8) {
            Text(verbatim: "AgentGauge")
                .font(.headline)
            Spacer(minLength: 8)
            Button {
                model.refreshNow()
            } label: {
                Image(systemName: "arrow.clockwise")
                    .font(.system(size: 13, weight: .medium))
                    .frame(width: 18, height: 18)
            }
            .iconButtonStyle()
            .keyboardShortcut("r")
            .help(L10n.get("Action_Refresh"))
            .accessibilityLabel(L10n.get("Action_Refresh"))

            Button {
                showSettings()
            } label: {
                Image(systemName: "gearshape")
                    .font(.system(size: 13, weight: .medium))
                    .frame(width: 18, height: 18)
            }
            .iconButtonStyle()
            .keyboardShortcut(",")
            .help(L10n.get("Action_Settings"))
            .accessibilityLabel(L10n.get("Action_Settings"))
        }
    }

    @ViewBuilder
    private var cards: some View {
        if model.states.isEmpty {
            VStack(spacing: 10) {
                Text(L10n.get("Empty_NoTools"))
                    .font(.callout)
                    .foregroundStyle(.secondary)
                Button(L10n.get("Action_Settings")) { showSettings() }
                    .cardActionStyle()
            }
            .frame(maxWidth: .infinity)
            .padding(.vertical, 20)
        } else {
            VStack(spacing: 10) {
                ForEach(model.states) { state in
                    ToolCard(
                        state: state,
                        basis: model.displayBasis,
                        now: model.now,
                        onSignIn: { model.signIn(state.tool) },
                        onRetry: { model.refreshNow() })
                }
            }
        }
    }

    private var footer: some View {
        HStack(spacing: 8) {
            Text(updatedText)
                .font(.caption)
                .foregroundStyle(.secondary)
                .monospacedDigit()
            Spacer(minLength: 8)
            Button(L10n.get("Action_Quit")) {
                NSApp.terminate(nil)
            }
            .buttonStyle(.borderless)
            .font(.caption)
            .foregroundStyle(.secondary)
            .keyboardShortcut("q")
        }
    }

    private var updatedText: String {
        guard let updated = model.lastUpdated else { return L10n.get("LastUpdated_Never") }
        let time = updated.formatted(Date.FormatStyle(date: .omitted, time: .shortened).locale(L10n.locale))
        return L10n.format("LastUpdated_At", time)
    }

    /// A menu bar app is never frontmost on its own, so it activates first; otherwise the
    /// Settings window opens behind whatever app was active.
    private func showSettings() {
        NSApp.activate()
        openSettings()
    }
}

struct ToolCard: View {
    let state: ToolState
    let basis: DisplayBasis
    let now: Date
    let onSignIn: () -> Void
    let onRetry: () -> Void

    private var windows: [UsageWindow] { state.snapshot?.windows ?? [] }

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            header
            content
        }
        .padding(Metrics.cardPadding)
        .frame(maxWidth: .infinity, alignment: .leading)
        .cardBackground()
    }

    private var header: some View {
        HStack(alignment: .firstTextBaseline, spacing: 6) {
            Text(verbatim: state.tool.displayName)
                .font(.headline)
            if let plan = state.snapshot?.plan {
                Text(verbatim: plan)
                    .font(.subheadline)
                    .foregroundStyle(.secondary)
            }
            Spacer(minLength: 8)
            if state.isRefreshing {
                ProgressView()
                    .controlSize(.mini)
                    .alignmentGuide(.firstTextBaseline) { $0[.bottom] - 2 }
            } else if state.lastRefreshFailed && !windows.isEmpty {
                Image(systemName: "exclamationmark.arrow.circlepath")
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .help(L10n.get("Card_RefreshFailed"))
                    .accessibilityLabel(L10n.get("Card_RefreshFailed"))
            }
            if let credits = state.snapshot?.resetCredits, credits > 0 {
                Text(L10n.format("ResetCredits", credits))
                    .font(.caption2.weight(.medium))
                    .padding(.horizontal, 7)
                    .padding(.vertical, 2)
                    .foregroundStyle(Color.accentColor)
                    .background(Color.accentColor.opacity(0.14), in: Capsule())
                    .help(L10n.format("Tooltip_ResetCredits", credits))
                    .accessibilityLabel(L10n.format("Tooltip_ResetCredits", credits))
            }
        }
    }

    @ViewBuilder
    private var content: some View {
        if windows.isEmpty {
            placeholder
        } else {
            let groups = WindowGrouping.groups(windows)
            ForEach(Array(groups.enumerated()), id: \.element.id) { index, group in
                if index > 0 { Divider() }
                VStack(alignment: .leading, spacing: 12) {
                    if let label = group.label {
                        Text(verbatim: label)
                            .font(.caption.weight(.semibold))
                            .foregroundStyle(.secondary)
                    }
                    ForEach(group.windows) { window in
                        UsageRow(window: window, basis: basis, now: now)
                    }
                }
            }
        }
    }

    @ViewBuilder
    private var placeholder: some View {
        switch state.auth {
        case .signedOut:
            message(L10n.get("Auth_Missing"), action: L10n.get("Action_SignIn"), perform: onSignIn)
        case .rejected:
            message(L10n.get("Auth_Expired"), action: L10n.get("Action_SignIn"), perform: onSignIn)
        case .unreadable:
            message(L10n.get("Auth_ReadFailed"), action: L10n.get("Action_SignIn"), perform: onSignIn)
        case .keychainDenied:
            message(L10n.get("Auth_KeychainDenied"), action: L10n.get("Action_TryAgain"), perform: onRetry)
        case .unknown, .signedIn:
            if state.isRefreshing || (state.auth == .unknown && !state.lastRefreshFailed) {
                HStack(spacing: 8) {
                    ProgressView().controlSize(.small)
                    Text(L10n.get("Loading"))
                }
                .font(.callout)
                .foregroundStyle(.secondary)
            } else {
                message(L10n.get(state.lastRefreshFailed ? "Card_RefreshFailed" : "NoData"),
                        action: L10n.get("Action_TryAgain"), perform: onRetry)
            }
        }
    }

    private func message(_ text: String, action: String, perform: @escaping () -> Void) -> some View {
        VStack(alignment: .leading, spacing: 10) {
            Text(text)
                .font(.callout)
                .foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)
            Button(action, action: perform)
                .cardActionStyle()
        }
    }
}

struct UsageRow: View {
    let window: UsageWindow
    let basis: DisplayBasis
    let now: Date

    var body: some View {
        let percent = basis.percent(for: window.usedRatio)
        let reset = ResetTimeFormatter.row(window.resetTime, now: now)
        let pace = PaceClassifier.caption(for: window, now: now)

        VStack(alignment: .leading, spacing: 6) {
            HStack(alignment: .firstTextBaseline) {
                Text(WindowLabels.label(for: window))
                    .font(.callout)
                Spacer(minLength: 8)
                Text(verbatim: "\(percent)%")
                    .font(.callout.weight(.semibold))
                    .monospacedDigit()
                    .contentTransition(.numericText(value: Double(percent)))
            }
            UsageBar(fraction: basis.fraction(for: window.usedRatio), level: UsageLevel(ratio: window.usedRatio))
            if !reset.isEmpty || pace != nil {
                HStack(spacing: 8) {
                    Text(reset)
                    Spacer(minLength: 8)
                    if let pace {
                        Text(pace).help(L10n.get("Pace_Tooltip"))
                    }
                }
                .font(.caption)
                .foregroundStyle(.secondary)
                .lineLimit(1)
            }
        }
        .animation(.smooth(duration: 0.35), value: percent)
        .accessibilityElement(children: .ignore)
        .accessibilityLabel(accessibilityText(percent: percent, reset: reset, pace: pace))
    }

    private func accessibilityText(percent: Int, reset: String, pace: String?) -> String {
        let name = [window.groupLabel, WindowLabels.label(for: window)].compactMap { $0 }.joined(separator: " ")
        let base = L10n.format(basis == .used ? "Ax_Used" : "Ax_Remaining", name, percent, reset)
        return [base, pace].compactMap { $0 }.joined(separator: ", ")
    }
}

/// A slim capsule meter. The fill keeps a minimum of its own height once non-zero so a
/// sliver of usage stays visible as a dot rather than vanishing.
struct UsageBar: View {
    let fraction: Double
    let level: UsageLevel

    var body: some View {
        GeometryReader { proxy in
            let clamped = min(max(fraction, 0), 1)
            ZStack(alignment: .leading) {
                Capsule().fill(.quaternary)
                Capsule()
                    .fill(level.tint)
                    .frame(width: clamped > 0 ? max(proxy.size.width * clamped, proxy.size.height) : 0)
            }
        }
        .frame(height: 6)
        .animation(.smooth(duration: 0.4), value: fraction)
        .accessibilityHidden(true)
    }
}
