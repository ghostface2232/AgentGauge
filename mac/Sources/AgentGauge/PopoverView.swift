import AppKit
import GaugeCore
import SwiftUI

/// The menu bar panel: a title row with refresh and settings, one card per shown tool, and
/// a quiet footer. It sizes to its cards and scrolls only past most of the screen height.
struct PopoverView: View {
    @Environment(AppModel.self) private var model
    @Environment(UpdateModel.self) private var update
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
            if update.isAvailable || update.isBusy {
                // Installing quits and relaunches the app, so it stays a deliberate click.
                Button {
                    update.performAction()
                } label: {
                    HStack(spacing: 4) {
                        if update.isBusy {
                            ProgressView().controlSize(.mini)
                        } else {
                            Image(systemName: "arrow.down.circle.fill")
                        }
                        Text(update.isBusy ? (update.statusText ?? "") : L10n.get("Update_Apply"))
                    }
                }
                .buttonStyle(.borderless)
                .font(.caption.weight(.medium))
                .foregroundStyle(Color.accentColor)
                .disabled(update.isBusy)
                .help(update.statusText ?? "")
            }
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
    @Environment(AppModel.self) private var model
    let state: ToolState
    let onSignIn: () -> Void
    let onRetry: () -> Void

    private var windows: [UsageWindow] { state.snapshot?.windows ?? [] }
    private var status: ProviderStatus? { model.serviceStatuses[state.tool].flatMap { $0.isVisible ? $0 : nil } }
    private var apiCost: (text: String, detail: String)? { model.apiCostText(for: state.tool) }
    private var credits: Int? { state.snapshot?.resetCredits.flatMap { $0 > 0 ? $0 : nil } }

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
        // With the cost caption, a status chip and a reset chip all present the row cannot fit
        // both chip labels, so both drop to their icons; help and accessibility keep the text.
        let chipLabels = !(apiCost != nil && status != nil && credits != nil)
        return HStack(alignment: .firstTextBaseline, spacing: 6) {
            Text(verbatim: state.tool.displayName)
                .font(.headline)
                .layoutPriority(1)
            if let plan = state.snapshot?.plan {
                Text(verbatim: plan)
                    .font(.subheadline)
                    .foregroundStyle(.secondary)
                    .lineLimit(1)
            }
            if let apiCost {
                Text(verbatim: apiCost.text)
                    .font(.subheadline)
                    .foregroundStyle(.tertiary)
                    .monospacedDigit()
                    .lineLimit(1)
                    .help(apiCost.detail)
                    .accessibilityLabel(apiCost.detail)
            }
            Spacer(minLength: 6)
            if model.isShowingRefresh(state) {
                ProgressView()
                    .controlSize(.mini)
                    .alignmentGuide(.firstTextBaseline) { $0[.bottom] - 2 }
                    .help(L10n.get("Status_Refreshing"))
            } else if state.lastRefreshFailed && !windows.isEmpty {
                Image(systemName: "exclamationmark.arrow.circlepath")
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .help(L10n.get("Tooltip_RefreshIssue"))
                    .accessibilityLabel(L10n.get("Tooltip_RefreshIssue"))
            }
            if let status {
                StatusChip(status: status, showsLabel: chipLabels)
                    .alignmentGuide(.firstTextBaseline) { $0[VerticalAlignment.center] + 4 }
            }
            if let credits {
                HeaderChip(systemImage: "arrow.counterclockwise",
                           label: L10n.format("ResetCredits", credits),
                           detail: L10n.format("Tooltip_ResetCredits", credits),
                           tint: .accentColor, showsLabel: chipLabels)
                    .alignmentGuide(.firstTextBaseline) { $0[VerticalAlignment.center] + 4 }
            }
        }
    }

    @ViewBuilder
    private var content: some View {
        if windows.isEmpty {
            placeholder
        } else {
            let groups = WindowGrouping.groups(windows)
            switch model.viewMode {
            case .gauge:
                GaugeGroupsView(groups: groups, basis: model.displayBasis) { model.details(for: state.tool, window: $0) }
            case .bar:
                ForEach(Array(groups.enumerated()), id: \.element.id) { index, group in
                    if index > 0 { Divider() }
                    VStack(alignment: .leading, spacing: 12) {
                        if let label = group.label {
                            Text(verbatim: label)
                                .font(.caption.weight(.semibold))
                                .foregroundStyle(.secondary)
                        }
                        ForEach(group.windows) { window in
                            UsageRow(window: window, details: model.details(for: state.tool, window: window),
                                     basis: model.displayBasis, showsSparkline: model.showSparkline)
                        }
                    }
                }
            }
        }
    }

    @ViewBuilder
    private var placeholder: some View {
        switch state.auth {
        case .signedOut:
            message(AuthText.missing(state.tool), action: AuthText.signInAction(state.tool), perform: onSignIn)
        case .rejected:
            message(AuthText.expired(state.tool), action: AuthText.signInAction(state.tool), perform: onSignIn)
        case .unreadable:
            message(AuthText.readFailed(state.tool), action: AuthText.signInAction(state.tool), perform: onSignIn)
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

/// A small tinted capsule in the card header: an account-level balance (reset credits) or a
/// flagged condition (service status). Icon-only when the header is crowded.
struct HeaderChip: View {
    let systemImage: String
    let label: String
    let detail: String
    let tint: Color
    let showsLabel: Bool

    var body: some View {
        HStack(spacing: 3) {
            Image(systemName: systemImage)
                .font(.system(size: 9, weight: .bold))
            if showsLabel {
                Text(label)
                    .font(.caption2.weight(.medium))
                    .lineLimit(1)
            }
        }
        .padding(.horizontal, showsLabel ? 7 : 5)
        .padding(.vertical, 3)
        .foregroundStyle(tint)
        .background(tint.opacity(0.14), in: Capsule())
        .fixedSize()
        .help(detail)
        .accessibilityElement(children: .ignore)
        .accessibilityLabel(detail)
    }
}

/// The public service-status chip; it opens the provider's status page. Its detail says the
/// health is service-wide and may be unrelated to this account's error.
struct StatusChip: View {
    let status: ProviderStatus
    let showsLabel: Bool

    var body: some View {
        if let url = status.tool.statusPageURL {
            Link(destination: url) { chip }
                .buttonStyle(.plain)
                .linkPointer()
                .accessibilityLabel("\(status.label). \(status.detail)")
                .accessibilityAddTraits(.isLink)
        } else {
            chip
        }
    }

    private var chip: some View {
        HeaderChip(systemImage: "exclamationmark.triangle.fill", label: status.label,
                   detail: status.detail, tint: .orange, showsLabel: showsLabel)
    }
}

/// Everything a row shows besides the bar itself, derived once per window by `AppModel`.
struct RowDetails {
    var reset: String
    var pace: String?
    var eta: String?
    var counts: String?
    var burndown: UsageBurndown?

    func accessibilityText(window: UsageWindow, basis: DisplayBasis) -> String {
        let name = [window.groupLabel, WindowLabels.label(for: window)].compactMap { $0 }.joined(separator: " ")
        let percent = basis.percent(for: window.usedRatio)
        let base = L10n.format(basis == .used ? "Ax_Used" : "Ax_Remaining", name, percent, reset)
        return [base, counts, pace, eta].compactMap { $0 }.joined(separator: ", ")
    }
}

struct UsageRow: View {
    let window: UsageWindow
    let details: RowDetails
    let basis: DisplayBasis
    let showsSparkline: Bool
    @State private var hoverText: String?

    var body: some View {
        let percent = basis.percent(for: window.usedRatio)

        VStack(alignment: .leading, spacing: 6) {
            HStack(alignment: .center, spacing: 8) {
                Text(WindowLabels.label(for: window))
                    .font(.callout)
                    .lineLimit(1)
                Spacer(minLength: 8)
                if showsSparkline, let burndown = details.burndown {
                    UsageSparkline(burndown: burndown, hoverText: $hoverText)
                        .frame(width: 64, height: 16)
                }
                Text(verbatim: "\(percent)%")
                    .font(.callout.weight(.semibold))
                    .monospacedDigit()
                    .contentTransition(.numericText(value: Double(percent)))
                    .frame(minWidth: 36, alignment: .trailing)
            }
            UsageBar(fraction: basis.fraction(for: window.usedRatio), level: UsageLevel(ratio: window.usedRatio))
            // The counts share the caption row when there is no pace to show, and move to the
            // ETA row otherwise, so a quota with counts never adds a near-empty line.
            let caption = hoverText ?? details.reset
            let trailing = details.pace ?? details.counts
            let countsBelow = details.pace != nil ? details.counts : nil
            if !caption.isEmpty || trailing != nil {
                HStack(spacing: 8) {
                    Text(caption)
                        .contentTransition(.opacity)
                    Spacer(minLength: 8)
                    if let pace = details.pace {
                        Text(pace).help(L10n.get("Pace_Tooltip"))
                    } else if let counts = details.counts {
                        Text(verbatim: counts).monospacedDigit()
                    }
                }
                .font(.caption)
                .foregroundStyle(.secondary)
                .lineLimit(1)
            }
            if details.eta != nil || countsBelow != nil {
                HStack(spacing: 8) {
                    if let eta = details.eta { Text(eta) }
                    Spacer(minLength: 8)
                    if let countsBelow {
                        Text(verbatim: countsBelow).monospacedDigit()
                    }
                }
                .font(.caption)
                .foregroundStyle(.secondary)
                .lineLimit(1)
            }
        }
        .animation(.smooth(duration: 0.35), value: percent)
        .accessibilityElement(children: .ignore)
        .accessibilityLabel(details.accessibilityText(window: window, basis: basis))
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
