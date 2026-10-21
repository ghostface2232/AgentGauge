import Foundation

/// The tools this build can track. The raw value is the persisted identity (cache,
/// hidden-tool list), so it must never change for an existing case.
public enum ToolKind: String, Codable, CaseIterable, Sendable, Identifiable {
    case claude
    case codex
    case cursor
    case antigravity
    case githubCopilot

    public var id: String { rawValue }

    /// Titled "Claude", not "Claude Code": Claude Code and Claude chat draw on one shared quota.
    public var displayName: String {
        switch self {
        case .claude: "Claude"
        case .codex: "Codex"
        case .cursor: "Cursor"
        case .antigravity: "Antigravity"
        case .githubCopilot: "GitHub Copilot"
        }
    }

    /// How the user signs in from settings or a signed-out card. Cursor and Antigravity have no
    /// CLI login; their sessions belong to their apps, so signing in means opening that app.
    public var signIn: SignInMethod {
        switch self {
        case .claude: .cli(CLICommand(executable: "claude", arguments: ["/login"]))
        case .codex: .cli(CLICommand(executable: "codex", arguments: ["login"]))
        case .cursor: .app(bundleIdentifier: "com.todesktop.230313mzl4w4u92", name: "Cursor")
        case .antigravity: .app(bundleIdentifier: "com.google.antigravity", name: "Antigravity")
        case .githubCopilot: .cli(CLICommand(executable: "gh", arguments: ["auth", "login"]))
        }
    }

    /// Claude and Codex are shown on a fresh install; the others are opt-in from settings.
    public var isShownByDefault: Bool {
        switch self {
        case .claude, .codex: true
        case .cursor, .antigravity, .githubCopilot: false
        }
    }

    /// Periodic refresh cadence. Codex's endpoint is the one its own CLI polls, so it can
    /// stay responsive; Claude's is throttled hard and its provider caches for 5 minutes
    /// anyway. Antigravity's read can launch a language server, and Copilot's quotas are
    /// monthly, so slow cadences lose nothing there.
    public var refreshInterval: TimeInterval {
        switch self {
        case .claude: 300
        case .codex: 180
        case .cursor: 300
        case .antigravity: 600
        case .githubCopilot: 900
        }
    }

    /// The minimum gap between popover-open reads. Zero for every tool whose read is one HTTP
    /// GET; Antigravity's read launches (and tears down) a language server when the app is
    /// closed, so without a floor every click on the menu bar item would spawn one. Explicit
    /// user actions and a wake bypass it.
    public var forcedRefreshFloor: TimeInterval {
        self == .antigravity ? 120 : 0
    }

    /// Public Statuspage-compatible service health page; nil means none is polled.
    public var statusPageURL: URL? {
        switch self {
        case .claude: URL(string: "https://status.anthropic.com/")
        case .codex: URL(string: "https://status.openai.com/")
        case .cursor: URL(string: "https://status.cursor.com/")
        case .githubCopilot: URL(string: "https://www.githubstatus.com/")
        case .antigravity: nil
        }
    }

    /// Whether the tool's CLI writes local session logs the API-equivalent cost reads.
    public var hasLocalCostLogs: Bool { self == .claude || self == .codex }
}

public enum SignInMethod: Sendable, Hashable {
    /// The official CLI's interactive sign-in, run in Terminal.
    case cli(CLICommand)
    /// An app that owns its own session; signing in happens inside it.
    case app(bundleIdentifier: String, name: String)
}

public struct CLICommand: Sendable, Hashable {
    public let executable: String
    public let arguments: [String]

    public init(executable: String, arguments: [String]) {
        self.executable = executable
        self.arguments = arguments
    }

    public var displayString: String { ([executable] + arguments).joined(separator: " ") }
}

/// The kind of usage window a tool exposes. Tools and plans differ, so the UI renders only
/// the windows a snapshot actually carries and never infers semantics from list position.
public enum UsageWindowType: String, Codable, Sendable {
    case fiveHour
    case weekly
    case modelQuota
    case billingCycle

    /// Display order inside a group: the 5-hour session before the weekly window.
    var sortOrder: Int {
        switch self {
        case .fiveHour: 0
        case .weekly: 1
        case .modelQuota: 2
        case .billingCycle: 3
        }
    }
}

/// One usage window (e.g. the 5-hour session or the weekly quota). `usedRatio` is the real
/// utilization the provider's official usage API reports, and `resetTime` its real reset.
public struct UsageWindow: Codable, Sendable, Hashable, Identifiable {
    public var type: UsageWindowType
    /// Provider-stable identity, set when a tool can expose two windows of one type
    /// (Claude's account-wide weekly beside a model-scoped weekly).
    public var providerID: String?
    /// Language-neutral family/model scope (e.g. "Fable"); windows sharing it are grouped.
    public var groupLabel: String?
    /// Utilization in [0, 1].
    public var usedRatio: Double
    /// Localization key for a label not derived from `type`. The key is persisted, never
    /// resolved text, so a rehydrated cache re-resolves in the active language.
    public var labelKey: String?
    public var resetTime: Date?
    /// Provider-reported window length in seconds, when known.
    public var duration: TimeInterval?
    /// Absolute counts behind `usedRatio` ("128 / 300"), when the provider reports both
    /// (Copilot's request entitlement). Never fabricated from a percent.
    public var usedCount: Int64?
    public var limitCount: Int64?

    public init(
        type: UsageWindowType,
        providerID: String? = nil,
        groupLabel: String? = nil,
        usedRatio: Double,
        labelKey: String? = nil,
        resetTime: Date? = nil,
        duration: TimeInterval? = nil,
        usedCount: Int64? = nil,
        limitCount: Int64? = nil
    ) {
        self.type = type
        self.providerID = providerID
        self.groupLabel = groupLabel
        self.usedRatio = usedRatio
        self.labelKey = labelKey
        self.resetTime = resetTime
        self.duration = duration
        self.usedCount = usedCount
        self.limitCount = limitCount
    }

    /// Identity across refreshes, notification history and the cache: the provider id when
    /// set, else the type. Two windows in one snapshot must have distinct keys.
    public var id: String { providerID ?? type.rawValue }
    public var key: String { id }
    public var label: String { WindowLabels.label(for: self) }
}

/// One tool's usage at one point in time — the single model the UI depends on, so a data
/// source can be swapped without touching the UI.
public struct UsageSnapshot: Codable, Sendable, Hashable {
    public var tool: ToolKind
    /// Plan label (e.g. "Max 5x", "Plus"); can be present without any window data.
    public var plan: String?
    public var windows: [UsageWindow]
    /// Manual rate-limit resets the account holds (Codex). Nil means the tool exposes no
    /// such allowance; 0 means it does and the balance is spent — the two stay distinct.
    public var resetCredits: Int?
    public var capturedAt: Date

    public init(tool: ToolKind, plan: String?, windows: [UsageWindow], resetCredits: Int? = nil, capturedAt: Date) {
        self.tool = tool
        self.plan = plan
        self.windows = windows
        self.resetCredits = resetCredits
        self.capturedAt = capturedAt
    }
}

/// Usage severity for the bar tint and the menu bar symbol. Always shown together with
/// the percent number, never by colour alone.
public enum UsageLevel: Int, Sendable, Comparable {
    case ok
    case caution
    case danger

    public static let cautionThreshold = 0.70
    public static let dangerThreshold = 0.90

    public init(ratio: Double) {
        self = ratio >= Self.dangerThreshold ? .danger
            : ratio >= Self.cautionThreshold ? .caution
            : .ok
    }

    public static func < (lhs: UsageLevel, rhs: UsageLevel) -> Bool { lhs.rawValue < rhs.rawValue }
}

/// Whether percentages read as used (an untouched window is 0%) or remaining (100%).
/// Only the shown number and fill flip; level, pace and notifications track the used share.
public enum DisplayBasis: String, Sendable, CaseIterable, Identifiable {
    case used
    case remaining

    public var id: String { rawValue }

    public func fraction(for usedRatio: Double) -> Double {
        let used = min(max(usedRatio, 0), 1)
        return self == .used ? used : 1 - used
    }

    /// An over-limit window floors at 0% remaining rather than going negative.
    public func percent(for usedRatio: Double) -> Int {
        Int((fraction(for: usedRatio) * 100).rounded())
    }
}

/// One app-wide way to draw every card's windows: horizontal bars (default) or circular gauges.
public enum UsageViewMode: String, Sendable, CaseIterable, Identifiable {
    case bar
    case gauge

    public var id: String { rawValue }
}
