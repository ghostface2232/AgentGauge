import GaugeCore
import ServiceManagement
import SwiftUI

/// The standard macOS Settings window (⌘,), a grouped form like System Settings.
struct SettingsView: View {
    @Environment(AppModel.self) private var model

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
                Toggle(L10n.get("Settings_ShowPercent"), isOn: $model.showPercentInMenuBar)
                Picker(L10n.get("Settings_DisplayBasis"), selection: $model.displayBasis) {
                    Text(L10n.get("Basis_Used")).tag(DisplayBasis.used)
                    Text(L10n.get("Basis_Remaining")).tag(DisplayBasis.remaining)
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

            Section {
                LabeledContent(L10n.get("Settings_Version"), value: Self.version)
                if let error = model.settingsError {
                    Text(error)
                        .font(.caption)
                        .foregroundStyle(.orange)
                }
            }
        }
        .formStyle(.grouped)
        .frame(width: 460)
        .fixedSize(horizontal: false, vertical: true)
        .onAppear { model.settingsAppeared() }
    }

    private static var version: String {
        Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "—"
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
                Button(L10n.get("Action_SignIn"), action: onSignIn)
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
        case .signedIn:
            if let plan = state.snapshot?.plan { return L10n.format("Auth_SignedInWithPlan", plan) }
            return L10n.get("Auth_SignedIn")
        case .signedOut: return L10n.get("Auth_Missing")
        case .rejected: return L10n.get("Auth_Expired")
        case .keychainDenied: return L10n.get("Auth_KeychainDenied")
        case .unreadable: return L10n.get("Auth_ReadFailed")
        case .unknown: return L10n.get("Auth_Checking")
        }
    }
}
