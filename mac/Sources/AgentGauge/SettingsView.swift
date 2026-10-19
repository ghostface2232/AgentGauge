import GaugeCore
import ServiceManagement
import SwiftUI

/// The standard macOS Settings window (⌘,), a grouped form like System Settings.
struct SettingsView: View {
    @Environment(AppModel.self) private var model
    @Environment(UpdateModel.self) private var update

    var body: some View {
        @Bindable var model = model
        Form {
            Section(L10n.get("Settings_General")) {
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
            }

            Section(L10n.get("Settings_Display")) {
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

            Section(L10n.get("Settings_Accounts")) {
                ForEach(ToolKind.allCases) { tool in
                    AccountRow(
                        tool: tool,
                        state: model.states.first { $0.tool == tool },
                        shown: !model.hiddenTools.contains(tool),
                        onShownChange: { model.setShown(tool, $0) },
                        onSignIn: { model.signIn(tool) })
                }
            }

            Section(L10n.get("Settings_About")) {
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
                if let error = model.settingsError {
                    Text(error)
                        .font(.caption)
                        .foregroundStyle(.orange)
                }
            }
        }
        .formStyle(.grouped)
        // The form scrolls: with display, notification, account and update sections it is
        // taller than a laptop screen, so it opens at a comfortable height and can grow.
        .frame(width: 460)
        .frame(minHeight: 420, idealHeight: 680)
        .onAppear { model.settingsAppeared() }
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
