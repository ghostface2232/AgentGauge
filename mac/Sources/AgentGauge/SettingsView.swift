import GaugeCore
import ServiceManagement
import SwiftUI

enum SettingsTab: Int, CaseIterable, Identifiable {
    case general
    case display
    case accounts
    case about

    var id: Int { rawValue }

    var title: String {
        switch self {
        case .general: L10n.get("Settings_General")
        case .display: L10n.get("Settings_Display")
        case .accounts: L10n.get("Settings_Accounts")
        case .about: L10n.get("Settings_About")
        }
    }

    var symbol: String {
        switch self {
        case .general: "gearshape"
        case .display: "gauge.with.dots.needle.50percent"
        case .accounts: "person.crop.circle"
        case .about: "info.circle"
        }
    }
}

/// The macOS Settings window (⌘,): toolbar tabs of short grouped forms, each sizing the window
/// to its own content so nothing scrolls or is cut off. A system TabView swaps panes and snaps
/// the window in one frame; this one moves the selection, the pane and the window together in
/// a single spring — the new pane unfolds from the top as the window eases to its height from
/// its top edge, and on macOS 26 the selection is a Liquid Glass bubble that flows between the
/// tabs. The title stays the app's name whichever tab is open.
struct SettingsView: View {
    @Environment(AppModel.self) private var model
    @Environment(\.accessibilityReduceMotion) private var reduceMotion
    @State private var tab: SettingsTab = .general

    static let spring = Animation.spring(response: 0.42, dampingFraction: 0.86)

    var body: some View {
        ZStack(alignment: .top) {
            pane(tab)
                .id(tab)
                .transition(paneTransition)
        }
        .navigationTitle(Text(verbatim: "AgentGauge"))
        .toolbar {
            SettingsTabBarItem(selection: Binding(get: { tab }, set: select))
        }
        .resizeFromTop()
        .onAppear { model.settingsAppeared() }
    }

    @ViewBuilder
    private func pane(_ tab: SettingsTab) -> some View {
        switch tab {
        case .general: GeneralSettings()
        case .display: DisplaySettings()
        case .accounts: AccountSettings()
        case .about: AboutSettings()
        }
    }

    /// The incoming pane unfolds downward with the window; the outgoing one only fades, so
    /// the two never travel past each other.
    private var paneTransition: AnyTransition {
        if reduceMotion { return .opacity }
        return .asymmetric(
            insertion: .modifier(active: PaneUnfold(revealed: 0), identity: PaneUnfold(revealed: 1)),
            removal: .opacity.animation(.easeOut(duration: 0.12)))
    }

    private func select(_ next: SettingsTab) {
        guard next != tab else { return }
        withAnimation(reduceMotion ? .easeInOut(duration: 0.2) : Self.spring) { tab = next }
    }
}

/// The pane revealed from its top edge down, fading in as it opens.
private struct PaneUnfold: ViewModifier, Animatable {
    var revealed: Double

    var animatableData: Double {
        get { revealed }
        set { revealed = newValue }
    }

    func body(content: Content) -> some View {
        content
            .mask(alignment: .top) {
                GeometryReader { proxy in
                    Rectangle().frame(height: proxy.size.height * max(revealed, 0))
                }
            }
            .opacity(min(1, revealed * 1.5))
    }
}

/// The tab bar, centred in the toolbar. On macOS 26 it brings its own glass (the toolbar's
/// shared capsule is hidden, so glass is not stacked on glass) and the selected tab is a
/// clear glass bubble that morphs from one tab to the next; earlier systems get a quiet
/// capsule highlight that slides.
private struct SettingsTabBarItem: ToolbarContent {
    @Binding var selection: SettingsTab

    var body: some ToolbarContent {
        #if compiler(>=6.2)
        if #available(macOS 26.0, *) {
            ToolbarItem(placement: .principal) { SettingsTabBar(selection: $selection) }
                .sharedBackgroundVisibility(.hidden)
        } else {
            ToolbarItem(placement: .principal) { SettingsTabBar(selection: $selection) }
        }
        #else
        ToolbarItem(placement: .principal) { SettingsTabBar(selection: $selection) }
        #endif
    }
}

private struct SettingsTabBar: View {
    @Binding var selection: SettingsTab
    @Namespace private var namespace

    var body: some View {
        #if compiler(>=6.2)
        if #available(macOS 26.0, *) {
            GlassEffectContainer(spacing: 6) {
                HStack(spacing: 2) {
                    ForEach(SettingsTab.allCases) { tab in
                        button(tab)
                            .background {
                                if tab == selection {
                                    Color.clear
                                        .glassEffect(.regular.interactive(), in: .capsule)
                                        .glassEffectID("selection", in: namespace)
                                }
                            }
                    }
                }
                .padding(3)
                .glassEffect(.regular, in: .capsule)
            }
        } else {
            fallback
        }
        #else
        fallback
        #endif
    }

    private var fallback: some View {
        HStack(spacing: 2) {
            ForEach(SettingsTab.allCases) { tab in
                button(tab)
                    .background {
                        if tab == selection {
                            Capsule()
                                .fill(.quaternary)
                                .matchedGeometryEffect(id: "selection", in: namespace)
                        }
                    }
            }
        }
        .padding(3)
        .background(.quaternary.opacity(0.6), in: Capsule())
    }

    private func button(_ tab: SettingsTab) -> some View {
        Button {
            selection = tab
        } label: {
            HStack(spacing: 5) {
                Image(systemName: tab.symbol)
                    .font(.system(size: 12, weight: .medium))
                    .symbolVariant(tab == selection ? .fill : .none)
                Text(tab.title)
                    .font(.system(size: 12, weight: tab == selection ? .semibold : .regular))
            }
            .foregroundStyle(tab == selection ? AnyShapeStyle(.primary) : AnyShapeStyle(.secondary))
            .padding(.horizontal, 11)
            .padding(.vertical, 5)
            .contentShape(Capsule())
        }
        .buttonStyle(.plain)
        .accessibilityAddTraits(tab == selection ? [.isButton, .isSelected] : .isButton)
    }
}

private extension View {
    /// The window grows and shrinks from its top edge, under the toolbar, rather than
    /// re-centring — so switching tabs reads as the sheet of settings extending downward.
    @ViewBuilder
    func resizeFromTop() -> some View {
        #if compiler(>=6.2)
        if #available(macOS 26.0, *) {
            windowResizeAnchor(.top)
        } else {
            self
        }
        #else
        self
        #endif
    }
}

private extension View {
    /// One tab's form: grouped like System Settings, fixed width, as tall as its content.
    func settingsPane() -> some View {
        formStyle(.grouped)
            .scrollDisabled(true)
            .frame(width: 460)
            .fixedSize(horizontal: false, vertical: true)
    }
}

private struct GeneralSettings: View {
    @Environment(AppModel.self) private var model

    var body: some View {
        @Bindable var model = model
        Form {
            Section {
                Toggle(L10n.get("Settings_LaunchAtLogin"), isOn: Binding(
                    get: { model.launchAtLogin },
                    set: { model.setLaunchAtLogin($0) }))
                if model.loginItemStatus == .requiresApproval {
                    HStack {
                        Text(L10n.get("Settings_LaunchAtLoginApproval"))
                            .font(.caption)
                            .foregroundStyle(.secondary)
                        Spacer()
                        Button(L10n.get("Action_OpenSystemSettings")) { model.openLoginItemSettings() }
                            .controlSize(.small)
                    }
                }
                if let error = model.settingsError {
                    Text(error)
                        .font(.caption)
                        .foregroundStyle(.orange)
                }
            }
            Section(L10n.get("Settings_Notifications")) {
                Toggle(L10n.get("Settings_NotifyThresholds"), isOn: $model.notifyThresholds)
                Toggle(L10n.get("Settings_NotifyResets"), isOn: $model.notifyResets)
                if model.notificationStatus == .denied && (model.notifyThresholds || model.notifyResets) {
                    HStack {
                        Text(L10n.get("Settings_NotificationsDenied"))
                            .font(.caption)
                            .foregroundStyle(.secondary)
                        Spacer()
                        Button(L10n.get("Action_OpenSystemSettings")) { model.openNotificationSettings() }
                            .controlSize(.small)
                    }
                }
            }
        }
        .settingsPane()
    }
}

private struct DisplaySettings: View {
    @Environment(AppModel.self) private var model

    var body: some View {
        @Bindable var model = model
        Form {
            Section {
                Toggle(L10n.get("Settings_ShowPercent"), isOn: $model.showPercentInMenuBar)
                Picker(L10n.get("Settings_DisplayBasis"), selection: $model.displayBasis) {
                    Text(L10n.get("Basis_Used")).tag(DisplayBasis.used)
                    Text(L10n.get("Basis_Remaining")).tag(DisplayBasis.remaining)
                }
                Picker(L10n.get("Settings_ViewMode"), selection: $model.viewMode) {
                    Text(L10n.get("ViewMode_Bar")).tag(UsageViewMode.bar)
                    Text(L10n.get("ViewMode_Gauge")).tag(UsageViewMode.gauge)
                }
                .pickerStyle(.segmented)
                Toggle(isOn: $model.showSparkline) {
                    Text(L10n.get("Settings_Sparkline"))
                    Text(L10n.get("Settings_SparklineHelp"))
                }
                .disabled(model.viewMode == .gauge)
                Picker(selection: $model.paceModel) {
                    ForEach(WeeklyPaceModel.allCases, id: \.self) { paceModel in
                        Text(paceModel.displayName).tag(paceModel)
                    }
                } label: {
                    Text(L10n.get("Settings_PaceModel"))
                    Text(L10n.get("Settings_PaceModelHelp"))
                }
                Toggle(isOn: $model.showApiCost) {
                    Text(L10n.get("Settings_ApiCost"))
                    Text(L10n.get("Settings_ApiCostHelp"))
                }
            }
        }
        .settingsPane()
    }
}

private struct AccountSettings: View {
    @Environment(AppModel.self) private var model

    var body: some View {
        Form {
            Section {
                ForEach(ToolKind.allCases) { tool in
                    AccountRow(
                        tool: tool,
                        state: model.states.first { $0.tool == tool },
                        shown: !model.hiddenTools.contains(tool),
                        onShownChange: { model.setShown(tool, $0) },
                        onSignIn: { model.signIn(tool) })
                }
            }
        }
        .settingsPane()
    }
}

private struct AboutSettings: View {
    @Environment(UpdateModel.self) private var update

    var body: some View {
        Form {
            Section {
                LabeledContent(L10n.get("Settings_Version")) {
                    HStack(spacing: 10) {
                        if update.isBusy {
                            ProgressView().controlSize(.small)
                        }
                        Text(verbatim: update.currentVersion)
                            .monospacedDigit()
                        Button(update.actionTitle) { update.performAction() }
                            .controlSize(.small)
                            .disabled(update.isBusy)
                    }
                }
                if let status = update.statusText {
                    Text(status)
                        .font(.caption)
                        .foregroundStyle(.secondary)
                }
            }
        }
        .settingsPane()
    }
}

private struct AccountRow: View {
    let tool: ToolKind
    let state: ToolState?
    let shown: Bool
    let onShownChange: (Bool) -> Void
    let onSignIn: () -> Void

    var body: some View {
        HStack(spacing: 12) {
            VStack(alignment: .leading, spacing: 2) {
                Text(verbatim: tool.displayName)
                if let status {
                    Text(status)
                        .font(.caption)
                        .foregroundStyle(.secondary)
                        .fixedSize(horizontal: false, vertical: true)
                }
            }
            Spacer(minLength: 12)
            if needsSignIn {
                Button(AuthText.signInAction(tool), action: onSignIn)
                    .controlSize(.small)
            }
            Toggle(L10n.get("Settings_ShowTool"), isOn: Binding(get: { shown }, set: onShownChange))
                .labelsHidden()
                .toggleStyle(.switch)
                .controlSize(.small)
                .help(L10n.get("Settings_ShowToolHelp"))
                .accessibilityLabel("\(tool.displayName) \(L10n.get("Settings_ShowTool"))")
        }
    }

    private var needsSignIn: Bool {
        guard let state else { return false }
        return [.signedOut, .rejected, .unreadable].contains(state.auth)
    }

    private var status: String? {
        guard let state, shown else { return nil }
        switch state.auth {
        case .signedIn where tool == .antigravity && state.snapshot?.windows.isEmpty != false:
            return L10n.get("Auth_Antigravity")
        case .signedIn:
            if let plan = state.snapshot?.plan { return L10n.format("Auth_SignedInWithPlan", plan) }
            return L10n.get("Auth_SignedIn")
        case .signedOut where tool == .antigravity: return L10n.get("Auth_Antigravity")
        case .signedOut: return AuthText.missing(tool)
        case .rejected: return AuthText.expired(tool)
        case .keychainDenied: return L10n.get("Auth_KeychainDenied")
        case .unreadable: return AuthText.readFailed(tool)
        case .unknown: return L10n.get("Auth_Checking")
        }
    }
}
