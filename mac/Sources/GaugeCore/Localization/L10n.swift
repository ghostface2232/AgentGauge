import Foundation

/// UI languages, in the column order of `Strings.table`. Do not reorder: the raw values
/// index the table's columns.
public enum AppLanguage: Int, Sendable, CaseIterable {
    case korean = 0
    case english = 1
    case japanese = 2
}

/// Translations live in code (`Strings.table`), mirroring the Windows app: the string
/// count is small, the language is fixed for the process lifetime, and one table keeps the
/// three languages reviewable side by side. The language follows macOS — the system
/// language order, or the per-app language chosen in System Settings › General ›
/// Language & Region › Applications (the bundle declares ko/en/ja for that list).
public enum L10n {
    /// Korean until `configure` runs, so tests assert deterministic Korean output.
    nonisolated(unsafe) public private(set) static var language: AppLanguage = .korean

    public static func configure(_ language: AppLanguage) {
        self.language = language
    }

    /// The first supported language in the user's preference order; English for anything else.
    public static func resolve(preferred: [String]) -> AppLanguage {
        for code in preferred {
            let lowered = code.lowercased()
            if lowered.hasPrefix("ko") { return .korean }
            if lowered.hasPrefix("ja") { return .japanese }
            if lowered.hasPrefix("en") { return .english }
        }
        return .english
    }

    public static var locale: Locale {
        switch language {
        case .korean: Locale(identifier: "ko_KR")
        case .english: Locale(identifier: "en_US")
        case .japanese: Locale(identifier: "ja_JP")
        }
    }

    /// The translation, falling back to English and then to the key itself, so a missing
    /// translation is visible rather than blank.
    public static func get(_ key: String) -> String {
        guard let row = Strings.table[key] else { return key }
        let cell = row.indices.contains(language.rawValue) ? row[language.rawValue] : ""
        if !cell.isEmpty { return cell }
        return row.indices.contains(AppLanguage.english.rawValue) ? row[AppLanguage.english.rawValue] : key
    }

    /// Replaces `{0}`, `{1}`, … with the arguments' descriptions.
    public static func format(_ key: String, _ arguments: any CustomStringConvertible...) -> String {
        var text = get(key)
        for (index, argument) in arguments.enumerated() {
            text = text.replacingOccurrences(of: "{\(index)}", with: argument.description)
        }
        return text
    }
}

/// Localized labels for usage windows. A window's own `labelKey` wins over the type-derived
/// default, so every surface (row, accessibility, notification title) names it the same way.
public enum WindowLabels {
    public static func label(for window: UsageWindow) -> String {
        label(for: window.type, labelKey: window.labelKey)
    }

    public static func label(for type: UsageWindowType, labelKey: String? = nil) -> String {
        if let labelKey, !labelKey.isEmpty { return L10n.get(labelKey) }
        switch type {
        case .fiveHour: return L10n.get("Label_FiveHour")
        case .weekly: return L10n.get("Label_Weekly")
        case .billingCycle: return L10n.get("Label_BillingCycle")
        case .modelQuota: return L10n.get("Label_ModelQuota")
        }
    }
}

/// Sign-in wording that depends on who owns the session: Cursor's lives in the Cursor app,
/// not a CLI, so its cards point there instead of at a CLI login.
public enum AuthText {
    public static func missing(_ tool: ToolKind) -> String {
        switch tool {
        case .cursor: L10n.get("Auth_Missing_Cursor")
        case .githubCopilot: L10n.get("Auth_Missing_Copilot")
        case .claude, .codex: L10n.get("Auth_Missing")
        }
    }

    public static func expired(_ tool: ToolKind) -> String {
        tool == .cursor ? L10n.get("Auth_Expired_Cursor") : L10n.get("Auth_Expired")
    }

    public static func readFailed(_ tool: ToolKind) -> String {
        tool == .cursor ? L10n.get("Auth_ReadFailed_Cursor") : L10n.get("Auth_ReadFailed")
    }

    public static func signInAction(_ tool: ToolKind) -> String {
        switch tool.signIn {
        case .cli: L10n.get("Action_SignIn")
        case .app(_, let name): L10n.format("Action_OpenApp", name)
        }
    }
}
