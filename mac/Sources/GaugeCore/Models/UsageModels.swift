import Foundation

/// The tools this build can track. The raw value is the persisted identity (cache,
/// hidden-tool list), so it must never change for an existing case.
public enum ToolKind: String, Codable, CaseIterable, Sendable, Identifiable {
    case claude
    case codex

    public var id: String { rawValue }

    /// Titled "Claude", not "Claude Code": Claude Code and Claude chat draw on one shared quota.
    public var displayName: String {
        switch self {
        case .claude: "Claude"
        case .codex: "Codex"
        }
    }

    /// The official CLI's interactive sign-in, run in Terminal from settings.
    public var loginCommand: CLICommand {
        switch self {
        case .claude: CLICommand(executable: "claude", arguments: ["/login"])
        case .codex: CLICommand(executable: "codex", arguments: ["login"])
        }
    }

    /// Periodic refresh cadence. Codex's endpoint is the one its own CLI polls, so it can
    /// stay responsive; Claude's is throttled hard and its provider caches for 5 minutes anyway.
    public var refreshInterval: TimeInterval {
        switch self {
        case .claude: 300
        case .codex: 180
        }
    }
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

    public init(
        type: UsageWindowType,
        providerID: String? = nil,
        groupLabel: String? = nil,
        usedRatio: Double,
        labelKey: String? = nil,
        resetTime: Date? = nil,
        duration: TimeInterval? = nil
    ) {
        self.type = type
        self.providerID = providerID
        self.groupLabel = groupLabel
        self.usedRatio = usedRatio
        self.labelKey = labelKey
        self.resetTime = resetTime
        self.duration = duration
    }

    /// Identity across refreshes, notification history and the cache: the provider id when
    /// set, else the type. Two windows in one snapshot must have distinct keys.
    public var id: String { providerID ?? type.rawValue }
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
