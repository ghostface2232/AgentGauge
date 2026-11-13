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
    @State private var canScrollUp = false
    @State private var canScrollDown = false
    @State private var scrollMetrics = ScrollMetrics()
    @State private var isScrolling = false
    @State private var isHoveringTrack = false
    @State private var isDragging = false
    @State private var dragStartProgress: CGFloat = 0
    @State private var dragStartLocationY: CGFloat = 0
    @State private var hideScrollerTask: Task<Void, Never>? = nil
    @State private var scrollController = ScrollController()

    private let headerHeight: CGFloat = 46
    private let footerHeight: CGFloat = 44

    private var totalHeight: CGFloat {
        min(max(contentHeight, 130), maximumScrollHeight)
    }

    var body: some View {
        ZStack(alignment: .top) {
            ScrollView(.vertical, showsIndicators: false) {
                cards
                    .padding(.horizontal, 12)
                    .padding(.top, headerHeight)
                    .padding(.bottom, footerHeight)
                    .background(ScrollViewBridge(controller: scrollController) { metrics in
                        handleScrollMetrics(metrics)
                    })
                    .onGeometryChange(for: CGFloat.self) { proxy in
                        proxy.size.height
                    } action: { height in
                        contentHeight = height
                    }
            }
            .scrollBounceBehavior(.basedOnSize)
            .frame(height: totalHeight)
            .mask(scrollMask)

            header
                .padding(.horizontal, 16)
                .frame(maxWidth: .infinity)
                .frame(height: headerHeight)

            VStack(spacing: 0) {
                Spacer()
                footer
                    .padding(.horizontal, 16)
                    .padding(.top, 10)
                    .padding(.bottom, 12)
                    .frame(maxWidth: .infinity)
            }
            .frame(height: totalHeight)

            customScrollIndicator
        }
        .frame(width: Metrics.panelWidth, height: totalHeight)
        // A clicked day keeps its highlight through keyboard focus, and nothing else in the
        // panel takes focus, so a click anywhere a control does not claim resigns it. Child
        // taps and buttons win over this one, so selecting a day still works.
        .contentShape(Rectangle())
        .onTapGesture { NSApp.keyWindow?.makeFirstResponder(nil) }
        .background(WindowKeyObserver { model.popoverOpened() })
    }

    private func handleScrollMetrics(_ newMetrics: ScrollMetrics) {
        guard newMetrics != scrollMetrics else { return }

        if canScrollUp != newMetrics.canScrollUp { canScrollUp = newMetrics.canScrollUp }
        if canScrollDown != newMetrics.canScrollDown { canScrollDown = newMetrics.canScrollDown }

        let didMove = abs(scrollMetrics.offsetY - newMetrics.offsetY) > 0.5
        scrollMetrics = newMetrics

        guard newMetrics.docHeight > newMetrics.viewportHeight + 1 else {
            isScrolling = false
            return
        }

        if didMove {
            if !isScrolling {
                withAnimation(.easeIn(duration: 0.12)) {
                    isScrolling = true
                }
            }
            if !isDragging && !isHoveringTrack {
                scheduleScrollerFadeOut()
            }
        }
    }

    private func scheduleScrollerFadeOut() {
        hideScrollerTask?.cancel()
        hideScrollerTask = Task {
            try? await Task.sleep(nanoseconds: 700_000_000)
            guard !Task.isCancelled else { return }
            await MainActor.run {
                if !isDragging && !isHoveringTrack {
                    withAnimation(.easeOut(duration: 0.3)) {
                        isScrolling = false
                    }
                }
            }
        }
    }

    private var customScrollIndicator: some View {
        let canScroll = scrollMetrics.docHeight > scrollMetrics.viewportHeight + 1
        let trackTop: CGFloat = headerHeight
        let trackBottom: CGFloat = totalHeight - footerHeight
        let trackHeight = max(trackBottom - trackTop, 0)
        let maxOffset = max(scrollMetrics.docHeight - scrollMetrics.viewportHeight, 1)
        let proportion = min(max(scrollMetrics.viewportHeight / max(scrollMetrics.docHeight, 1), 0.08), 1.0)
        let knobHeight = min(max(trackHeight * proportion, 24), trackHeight)
        let travel = max(trackHeight - knobHeight, 0)
        let progress = min(max(scrollMetrics.offsetY / maxOffset, 0.0), 1.0)
        let knobY = trackTop + travel * progress

        let isVisible = canScroll && (isScrolling || isHoveringTrack || isDragging)

        return ZStack(alignment: .topTrailing) {
            if canScroll {
                // Interactive hit area along the right track (width: 14pt)
                Color.clear
                    .frame(width: 14, height: trackHeight)
                    .contentShape(Rectangle())
                    .position(x: Metrics.panelWidth - 7, y: trackTop + trackHeight / 2)
                    .gesture(
                        DragGesture(minimumDistance: 0)
                            .onChanged { value in
                                hideScrollerTask?.cancel()
                                if !isDragging {
                                    isDragging = true
                                    isScrolling = true
                                    let clickY = value.startLocation.y
                                    let currentKnobTop = travel * progress
                                    let currentKnobBottom = currentKnobTop + knobHeight

                                    if clickY >= currentKnobTop && clickY <= currentKnobBottom {
                                        dragStartProgress = progress
                                        dragStartLocationY = clickY
                                    } else {
                                        let targetKnobTop = clickY - (knobHeight / 2)
                                        let jumpedProgress = travel > 0 ? min(max(targetKnobTop / travel, 0.0), 1.0) : 0
                                        dragStartProgress = jumpedProgress
                                        dragStartLocationY = clickY
                                        scrollController.scroll(to: jumpedProgress * maxOffset)
                                    }
                                } else {
                                    let deltaY = value.location.y - dragStartLocationY
                                    let deltaProgress = travel > 0 ? deltaY / travel : 0
                                    let newProgress = min(max(dragStartProgress + deltaProgress, 0.0), 1.0)
                                    scrollController.scroll(to: newProgress * maxOffset)
                                }
                            }
                            .onEnded { _ in
                                isDragging = false
                                scheduleScrollerFadeOut()
                            }
                    )
                    .onHover { hovering in
                        isHoveringTrack = hovering
                        if hovering {
                            hideScrollerTask?.cancel()
                            withAnimation(.easeIn(duration: 0.12)) {
                                isScrolling = true
                            }
                        } else {
                            scheduleScrollerFadeOut()
                        }
                    }

                // Visual pill knob
                if isVisible {
                    Capsule()
                        .fill(Color.primary.opacity(isDragging ? 0.45 : (isHoveringTrack ? 0.35 : 0.25)))
                        .frame(width: 4.0, height: knobHeight)
                        .position(x: Metrics.panelWidth - 6.0, y: knobY + knobHeight / 2)
                        .allowsHitTesting(false)
                        .transition(.opacity)
                }
            }
        }
        .accessibilityElement(children: .ignore)
        .accessibilityLabel(Text("Scroll Bar"))
        .accessibilityValue(Text("\(Int(progress * 100))%"))
        .accessibilityAdjustableAction { direction in
            let step: CGFloat = 40
            switch direction {
            case .increment:
                scrollController.scroll(to: scrollMetrics.offsetY + step)
            case .decrement:
                scrollController.scroll(to: scrollMetrics.offsetY - step)
            @unknown default:
                break
            }
        }
        .animation(.easeInOut(duration: 0.2), value: isVisible)
    }

    private var scrollMask: some View {
        GeometryReader { proxy in
            let height = proxy.size.height
            let topCutoff = 42.0 / max(height, 1)
            let topMid = 51.0 / max(height, 1)
            let topEnd = 64.0 / max(height, 1)
            let bottomEnd = (height - 64.0) / max(height, 1)
            let bottomMid = (height - 50.0) / max(height, 1)
            let bottomCutoff = (height - 40.0) / max(height, 1)

            LinearGradient(
                stops: [
                    .init(color: .clear, location: 0.0),
                    .init(color: .clear, location: topCutoff),
                    .init(color: canScrollUp ? .clear : .black, location: topCutoff),
                    .init(color: canScrollUp ? .black.opacity(0.4) : .black, location: topMid),
                    .init(color: .black, location: topEnd),
                    .init(color: .black, location: bottomEnd),
                    .init(color: canScrollDown ? .black.opacity(0.4) : .black, location: bottomMid),
                    .init(color: canScrollDown ? .clear : .black, location: bottomCutoff),
                    .init(color: .clear, location: bottomCutoff),
                    .init(color: .clear, location: 1.0)
                ],
                startPoint: .top,
                endPoint: .bottom
            )
            .animation(.easeOut(duration: 0.24), value: canScrollUp)
            .animation(.easeOut(duration: 0.24), value: canScrollDown)
        }
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
            if model.canShowDailyUsage {
                Button {
                    model.showDailyUsage.toggle()
                } label: {
                    Image(systemName: "calendar")
                        .font(.system(size: 13, weight: .medium))
                        .foregroundStyle(model.showDailyUsage ? Color.accentColor : Color.primary)
                        .frame(width: 18, height: 18)
                }
                .iconButtonStyle()
                .accessibilityLabel(L10n.get("DailyUsage_Label"))
                .accessibilityAddTraits(model.showDailyUsage ? .isSelected : [])
                .help(L10n.get("DailyUsage_Help"))
            }
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
                        onSignIn: {
                            if let account = state.account { model.signIn(account) }
                            else { model.signIn(state.tool) }
                        },
                        onRetry: {
                            if state.account != nil { model.refreshAccount(state.id) }
                            else { model.refreshNow() }
                        })
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
                .lineLimit(1)
                .help(updatedText)
            Spacer(minLength: 8)
            if update.isAvailable || update.isBusy || update.hasFailedInstall {
                // Installing quits and relaunches the app, so it stays a deliberate click.
                Button {
                    update.performAction()
                } label: {
                    HStack(spacing: 4) {
                        if update.isBusy {
                            ProgressView().controlSize(.mini)
                        } else {
                            Image(systemName: update.hasFailedInstall ? "exclamationmark.circle.fill" : "arrow.down.circle.fill")
                        }
                        Text(update.isAvailable ? L10n.get("Update_Apply")
                             : update.hasFailedInstall ? L10n.get("Update_Failed") : (update.statusText ?? ""))
                            .lineLimit(1)
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
            .buttonStyle(.plain)
            .font(.caption)
            .foregroundStyle(.secondary)
            .padding(.horizontal, 9)
            .padding(.vertical, 4)
            .background(
                RoundedRectangle(cornerRadius: 6, style: .continuous)
                    .fill(Color.primary.opacity(0.06))
            )
            .overlay(
                RoundedRectangle(cornerRadius: 6, style: .continuous)
                    .strokeBorder(Color.primary.opacity(0.08), lineWidth: 0.5)
            )
            .contentShape(RoundedRectangle(cornerRadius: 6, style: .continuous))
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
    private var apiCost: (text: String, detail: String, accessibility: String)? {
        state.account == nil ? model.apiCostText(for: state.tool) : nil
    }
    private var heldResets: Int {
        ResetCreditDeadline.availableCount(balance: state.snapshot?.resetCredits,
                                           deadlines: state.snapshot?.resetCreditDeadlines, now: model.now)
    }
    private var resetBalanceUnconfirmed: Bool {
        guard let snapshot = state.snapshot else { return false }
        return ResetCreditDeadline.isBalanceUnconfirmed(balance: snapshot.resetCredits, deadlines: snapshot.resetCreditDeadlines,
                                                       capturedAt: snapshot.capturedAt, now: model.now)
    }
    private var creditWarning: ResetCreditDeadline? {
        ResetCreditDeadline.upcoming(state.snapshot?.resetCreditDeadlines, now: model.now)
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            header
            if let account = state.account {
                Text(verbatim: account.label)
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }
            let supplemental = supplementalUsage
            if !windows.isEmpty || (supplemental.isEmpty && heldResets == 0) || state.lastRefreshFailed { content }
            if !supplemental.isEmpty {
                Text(verbatim: supplemental)
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }
        }
        .padding(Metrics.cardPadding)
        .frame(maxWidth: .infinity, alignment: .leading)
        .cardBackground()
    }

    /// Per-scope breakdown for the chip's tooltip ("Fable · 1 available resets").
    private var resetBalanceDetail: String {
        if resetBalanceUnconfirmed {
            return L10n.format("Usage_ResetBalanceLastKnown", heldResets.formatted(.number.locale(L10n.locale)))
        }
        let deadlines = state.snapshot?.resetCreditDeadlines?.filter {
            $0.expiresAt > model.now && ($0.count ?? 0) > 0
        } ?? []
        let scopes = Dictionary(grouping: deadlines, by: \.scope)
        let lines = scopes.keys.sorted { ($0 ?? "") < ($1 ?? "") }.map { scope in
            let count = scopes[scope, default: []].reduce(0) { $0 + ($1.count ?? 0) }
            let label = L10n.format("Usage_ResetBalance", count.formatted(.number.locale(L10n.locale)))
            return scope.map { "\($0) · \(label)" } ?? label
        }
        return lines.isEmpty ? L10n.format("Usage_ResetBalance", heldResets.formatted(.number.locale(L10n.locale)))
            : lines.joined(separator: "\n")
    }

    private var supplementalUsage: String {
        var lines: [String] = []
        if let source = state.snapshot?.sourceLabel {
            lines.append(L10n.format("Usage_Source", source))
        }
        if let credits = state.snapshot?.aiCreditsUsed {
            let amount = credits.formatted(.number.precision(.fractionLength(0...2)).locale(L10n.locale))
            lines.append(L10n.format("Usage_AiCreditsUsed", amount))
        }
        return lines.joined(separator: "\n")
    }

    private var header: some View {
        // Three or more header annotations no longer fit with their labels, so the chips go icon-only.
        let chipLabels = !(apiCost != nil && status != nil && heldResets > 0)
        return HStack(alignment: .firstTextBaseline, spacing: 6) {
            ToolLogoView(tool: state.tool)
                .foregroundStyle(.primary)
                .frame(width: 15, height: 15)
                .alignmentGuide(.firstTextBaseline) { $0[VerticalAlignment.center] + 5 }
            Text(verbatim: state.tool.displayName)
                .font(.headline)
                .layoutPriority(1)
            if let plan = state.snapshot?.plan
                ?? (state.snapshot?.planUnavailable == true ? L10n.get("Plan_Unconfirmed") : nil) {
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
                    .accessibilityLabel(apiCost.accessibility)
            }
            Spacer(minLength: 6)
            // Both can show at once: stale, but a retry is running.
            if state.lastRefreshFailed && (!windows.isEmpty || !supplementalUsage.isEmpty || heldResets > 0) {
                Image(systemName: "exclamationmark.arrow.circlepath")
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .help(L10n.get("Tooltip_RefreshIssue"))
                    .accessibilityLabel(L10n.get("Tooltip_RefreshIssue"))
            }
            if model.isShowingRefresh(state) {
                ProgressView()
                    .controlSize(.mini)
                    .alignmentGuide(.firstTextBaseline) { $0[.bottom] - 2 }
                    .help(L10n.get("Status_Refreshing"))
            }
            if let status {
                StatusChip(status: status)
                    .alignmentGuide(.firstTextBaseline) { $0[VerticalAlignment.center] + 4 }
            }
            // One chip: the blue balance, which the expiry warning recolours in place once it applies.
            if let creditWarning {
                let remaining = ResetCreditText.remaining(creditWarning, now: model.now)
                HeaderChip(systemImage: "arrow.counterclockwise",
                           label: chipLabels ? L10n.format("ResetCredit_Label", remaining) : remaining,
                           detail: "\(ResetCreditText.description(creditWarning))\n\(resetBalanceDetail)",
                           tint: creditWarning.expiresAt.timeIntervalSince(model.now) <= 86_400 ? .red : .orange,
                           showsLabel: true)
                    .alignmentGuide(.firstTextBaseline) { $0[VerticalAlignment.center] + 4 }
            } else if heldResets > 0 {
                HeaderChip(systemImage: "arrow.counterclockwise",
                           label: L10n.format(resetBalanceUnconfirmed ? "Usage_ResetBalanceLastKnown" : "ResetCredits",
                                              heldResets.formatted(.number.locale(L10n.locale))),
                           detail: resetBalanceDetail,
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
            switch model.effectiveViewMode {
            case .gauge:
                GaugeGroupsView(groups: groups, basis: model.displayBasis) { model.details(for: state, window: $0) }
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
                            UsageRow(window: window, details: model.details(for: state, window: window),
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
        case .signedOut, .rejected, .unreadable:
            let text = state.auth == .signedOut ? AuthText.missing(state.tool)
                : (state.auth == .rejected ? AuthText.expired(state.tool) : AuthText.readFailed(state.tool))
            if state.account != nil && state.tool == .githubCopilot {
                message(L10n.get("Account_CopilotSignIn"), action: L10n.get("Action_TryAgain"), perform: onRetry)
            } else if model.signingIn.contains(state.id) {
                Text(L10n.get("Account_LoginRunning")).font(.callout).foregroundStyle(.secondary)
            } else {
                let failed = model.failedAccountSignIns.contains(state.id)
                message(failed ? L10n.get("Account_LoginFailed") : text,
                        action: AuthText.signInAction(state.tool), enabled: model.canSignIn(state.id), perform: onSignIn)
            }
        case .keychainDenied:
            message(L10n.get("Auth_KeychainDenied"), action: L10n.get("Action_TryAgain"), perform: onRetry)
        case .unknown, .signedIn:
            if state.isRefreshing || (state.auth == .unknown && !state.lastRefreshFailed && !state.lastRefreshSkipped) {
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

    private func message(_ text: String, action: String, enabled: Bool = true, perform: @escaping () -> Void) -> some View {
        VStack(alignment: .leading, spacing: 10) {
            Text(text)
                .font(.callout)
                .foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)
            Button(action, action: perform).disabled(!enabled)
                .cardActionStyle()
        }
    }
}

/// A tinted capsule for an expiring reset or a service incident.
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
        // Fixed so an icon-only chip matches a labelled one, whose text line is taller than its icon.
        .frame(height: Metrics.headerChipHeight)
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
                   detail: status.detail, tint: .orange, showsLabel: false)
    }
}

struct DailyBarSegment: Identifiable {
    let id: Date
    let fraction: Double
    let description: String

    init(segment: DailyUsageSegment, cost: ApiCostDay?) {
        id = segment.day
        fraction = segment.usedRatio
        let date = segment.day.formatted(.dateTime.month(.abbreviated).day().locale(L10n.locale))
        let percent = (fraction * 100).formatted(.number.precision(.fractionLength(0...1)).locale(L10n.locale))
        var text = L10n.format("DailyUsage_Day", date, percent)
        if let cost {
            let amount = String(format: "%.2f", NSDecimalNumber(decimal: cost.costUSD).doubleValue)
            text += " · " + L10n.format(cost.isFloor ? "DailyUsage_CostFloor" : "DailyUsage_Cost", amount)
        }
        description = text
    }
}

/// Everything a row shows besides the bar itself, derived once per window by `AppModel`.
struct RowDetails {
    var reset: String
    var pace: String?
    var eta: String?
    var counts: String?
    var burndown: UsageBurndown?
    var dailySegments: [DailyBarSegment] = []

    func accessibilityText(window: UsageWindow, basis: DisplayBasis) -> String {
        let name = [window.groupLabel, WindowLabels.label(for: window)].compactMap { $0 }.joined(separator: " ")
        let percent = basis.percent(for: window.usedRatio)
        let base = L10n.format(basis == .used ? "Ax_Used" : "Ax_Remaining", name, percent, reset)
        return ([base, counts, pace, eta].compactMap { $0 } + dailySegments.map(\.description)).joined(separator: ", ")
    }
}

struct UsageRow: View {
    let window: UsageWindow
    let details: RowDetails
    let basis: DisplayBasis
    let showsSparkline: Bool
    @State private var hoverText: String?
    @State private var hoveredDay: Date?
    @FocusState private var focusedDay: Date?

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
            UsageBar(fraction: basis.fraction(for: window.usedRatio), level: UsageLevel(ratio: window.usedRatio),
                     basis: basis, dailySegments: details.dailySegments, hoveredDay: $hoveredDay, focusedDay: $focusedDay)
            // The counts share the caption row when there is no pace to show, and move to the
            // ETA row otherwise, so a quota with counts never adds a near-empty line.
            let day = focusedDay ?? hoveredDay
            let dailyCaption = details.dailySegments.first { $0.id == day }?.description
            let caption = dailyCaption ?? hoverText ?? details.reset
            let trailing = details.pace ?? details.counts
            let countsBelow = details.pace != nil ? details.counts : nil
            if !caption.isEmpty || trailing != nil {
                HStack(spacing: 8) {
                    Text(caption)
                        .contentTransition(.opacity)
                        .help(caption)
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
    let basis: DisplayBasis
    let dailySegments: [DailyBarSegment]
    @Binding var hoveredDay: Date?
    let focusedDay: FocusState<Date?>.Binding

    private let segmentGap: CGFloat = 2

    var body: some View {
        GeometryReader { proxy in
            let clamped = min(max(fraction, 0), 1)
            ZStack(alignment: .leading) {
                Capsule().fill(.quaternary)
                if dailySegments.isEmpty {
                    Capsule()
                        .fill(level.tint)
                        .frame(width: clamped > 0 ? max(proxy.size.width * clamped, proxy.size.height) : 0)
                } else {
                    let unattributedWidth = basis == .used ? proxy.size.width *
                        UsageDailyBreakdown.unattributedUsedRatio(
                            clamped, datedRatio: dailySegments.reduce(0) { $0 + $1.fraction }) : 0
                    let unattributedGap = min(segmentGap / 2, unattributedWidth / 4)
                    let selectedDay = focusedDay.wrappedValue
                    let hasSelection = dailySegments.contains { $0.id == selectedDay }
                    let unselectedOpacity = hasSelection ? 0.42 : 1.0
                    HStack(spacing: 0) {
                        if basis == .remaining {
                            Capsule().fill(level.tint.opacity(unselectedOpacity))
                                .frame(width: proxy.size.width * clamped)
                        }
                        ForEach(Array(dailySegments.enumerated()), id: \.element.id) { index, segment in
                            let width = proxy.size.width * segment.fraction
                            let halfGap = min(segmentGap, width / 4) / 2
                            let leading = index == 0 ? 0 : halfGap
                            let trailing = index == dailySegments.count - 1 ? min(halfGap, unattributedGap) : halfGap
                            let opacity = hasSelection
                                ? (segment.id == selectedDay ? 1.0 : unselectedOpacity)
                                : (index.isMultiple(of: 2) ? 1.0 : 0.72)
                            RoundedRectangle(cornerRadius: 2.5)
                                .fill(level.tint.opacity(basis == .remaining ? opacity * 0.5 : opacity))
                                .frame(width: width - leading - trailing)
                                .padding(.leading, leading)
                                .padding(.trailing, trailing)
                                .contentShape(Rectangle())
                                .help(segment.description)
                                .onHover { inside in
                                    if inside { hoveredDay = segment.id }
                                    else if hoveredDay == segment.id { hoveredDay = nil }
                                }
                                .onTapGesture { focusedDay.wrappedValue = segment.id }
                                .focusable()
                                .focused(focusedDay, equals: segment.id)
                        }
                        if unattributedWidth > 0 {
                            // The undated share keeps the total fill accurate without a day tooltip.
                            RoundedRectangle(cornerRadius: 2.5)
                                .fill(level.tint.opacity(0.5 * unselectedOpacity))
                                .frame(width: unattributedWidth - unattributedGap)
                                .padding(.leading, unattributedGap)
                                .allowsHitTesting(false)
                        }
                    }
                }
            }
        }
        .frame(height: 6)
        .focusEffectDisabled()
        .animation(.smooth(duration: 0.4), value: fraction)
        .accessibilityHidden(true)
    }
}

@MainActor
final class ScrollController {
    weak var scrollView: NSScrollView?

    func scroll(to offsetY: CGFloat) {
        guard let scrollView,
              let docView = scrollView.documentView else { return }
        let contentView = scrollView.contentView
        let maxOffset = max(docView.bounds.height - contentView.bounds.height, 0)
        let clampedY = min(max(offsetY, 0), maxOffset)
        contentView.scroll(to: NSPoint(x: 0, y: clampedY))
        scrollView.reflectScrolledClipView(contentView)
    }
}

struct ScrollMetrics: Equatable {
    var offsetY: CGFloat = 0
    var viewportHeight: CGFloat = 1
    var docHeight: CGFloat = 1
    var canScrollUp: Bool = false
    var canScrollDown: Bool = false
}

/// Bridges NSScrollView to SwiftUI: suppresses default thick/black overlay scrollers
/// and observes scroll bounds changes in real time for custom indicator and gradient mask.
private struct ScrollViewBridge: NSViewRepresentable {
    var controller: ScrollController
    var onMetricsChanged: (ScrollMetrics) -> Void

    func makeNSView(context: Context) -> ObserverView {
        let view = ObserverView()
        view.controller = controller
        view.onMetricsChanged = onMetricsChanged
        return view
    }

    func updateNSView(_ nsView: ObserverView, context: Context) {
        nsView.controller = controller
        nsView.onMetricsChanged = onMetricsChanged
        nsView.ensureAttached()
        nsView.updateState()
    }

    final class ObserverView: NSView {
        weak var controller: ScrollController?
        var onMetricsChanged: ((ScrollMetrics) -> Void)?
        private(set) weak var observedScrollView: NSScrollView?
        private var lastMetrics: ScrollMetrics?

        override func viewDidMoveToSuperview() {
            super.viewDidMoveToSuperview()
            attach()
        }

        override func viewDidMoveToWindow() {
            super.viewDidMoveToWindow()
            attach()
        }

        func ensureAttached() {
            if observedScrollView == nil {
                attach()
            }
        }

        private func attach() {
            guard let scrollView = enclosingScrollView else { return }
            if observedScrollView === scrollView { return }
            detach()
            observedScrollView = scrollView
            controller?.scrollView = scrollView

            // Suppress default scroller so our custom slim indicator renders cleanly
            scrollView.hasVerticalScroller = false
            scrollView.hasHorizontalScroller = false
            scrollView.postsBoundsChangedNotifications = true
            scrollView.contentView.postsBoundsChangedNotifications = true

            NotificationCenter.default.addObserver(
                self,
                selector: #selector(handleScroll),
                name: NSView.boundsDidChangeNotification,
                object: scrollView.contentView)

            updateState()
        }

        private func detach() {
            if let scrollView = observedScrollView {
                NotificationCenter.default.removeObserver(
                    self,
                    name: NSView.boundsDidChangeNotification,
                    object: scrollView.contentView)
                observedScrollView = nil
                if controller?.scrollView === scrollView {
                    controller?.scrollView = nil
                }
            }
        }

        deinit {
            detach()
        }

        @objc private func handleScroll() {
            updateState()
        }

        func updateState() {
            guard let scrollView = observedScrollView ?? enclosingScrollView,
                  let docView = scrollView.documentView else { return }

            if observedScrollView == nil {
                attach()
            }

            scrollView.hasVerticalScroller = false
            scrollView.hasHorizontalScroller = false

            let offsetY = scrollView.contentView.bounds.origin.y
            let viewportHeight = scrollView.contentView.bounds.height
            let docHeight = docView.bounds.height

            let canScroll = docHeight > viewportHeight + 1
            let canScrollUp = canScroll && offsetY > 3
            let canScrollDown = canScroll && (offsetY + viewportHeight < docHeight - 3)

            let metrics = ScrollMetrics(
                offsetY: offsetY,
                viewportHeight: viewportHeight,
                docHeight: docHeight,
                canScrollUp: canScrollUp,
                canScrollDown: canScrollDown
            )

            guard metrics != lastMetrics else { return }
            lastMetrics = metrics

            DispatchQueue.main.async { [weak self] in
                self?.onMetricsChanged?(metrics)
            }
        }
    }
}
