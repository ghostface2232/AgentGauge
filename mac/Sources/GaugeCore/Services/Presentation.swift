import Foundation

/// Windows that share a `groupLabel` (a model/family scope such as "Fable"), shown together
/// under that heading. Account-wide windows have no heading and come first.
public struct WindowGroup: Sendable, Equatable, Identifiable {
    public let label: String?
    public let windows: [UsageWindow]

    public var id: String { label ?? "" }
}

public enum WindowGrouping {
    public static func groups(_ windows: [UsageWindow]) -> [WindowGroup] {
        var order: [String?] = []
        var members: [String: [UsageWindow]] = [:]
        for window in windows {
            let label = ProviderText.normalize(window.groupLabel)
            let key = label ?? ""
            if members[key] == nil {
                order.append(label)
                members[key] = []
            }
            members[key]?.append(window)
        }
        if let index = order.firstIndex(where: { $0 == nil }), index != 0 {
            order.insert(order.remove(at: index), at: 0)
        }
        return order.map { label in
            // Stable sort: 5-hour before weekly, provider order otherwise.
            let windows = (members[label ?? ""] ?? []).enumerated()
                .sorted { ($0.element.type.sortOrder, $0.offset) < ($1.element.type.sortOrder, $1.offset) }
                .map(\.element)
            return WindowGroup(label: label, windows: windows)
        }
    }
}

/// What the menu bar item shows: a tool's logo ring (`ring`), whose arcs carry its 5-hour and
/// weekly windows. The chosen tool's, or — for "auto" — whichever tool has used the most of
/// its quota right now, so the state is glanceable without opening the panel.
public struct MenuBarSummary: Sendable, Equatable {
    public let highestUsedRatio: Double?
    /// The ring to draw, or nil when no shown tool has a usable window yet.
    public let ring: StatusRing?
    /// One line per tool ("Claude  5H 32% · W 15%"), for the hover tooltip.
    public let tooltip: String

    public init(states: [ToolState], targetKey: String? = nil) {
        let target = StatusTarget.tool(from: targetKey)
        // A target with no usable windows yet is unavailable (as in Settings), so the icon
        // follows the most-used tool instead.
        var chosen: StatusRing?
        if let target, let state = states.first(where: { $0.tool == target }) {
            chosen = StatusRing(state: state)
        }
        if chosen == nil {
            chosen = states.compactMap { StatusRing(state: $0) }.mostUsed
        }
        tooltip = Self.tooltipText(for: states)
        if let chosen {
            ring = chosen
            // The level follows the arc that moves first: the 5-hour window, or the long one
            // for a tool that has none.
            highestUsedRatio = (chosen.short ?? chosen.long).map(\.usedRatio)
            return
        }
        ring = nil
        highestUsedRatio = states
            .flatMap { $0.snapshot?.windows ?? [] }
            .map(\.usedRatio)
            .filter(\.isFinite)
            .max()
    }

    private static func tooltipText(for states: [ToolState]) -> String {
        states.compactMap { state -> String? in
            guard let ring = StatusRing(state: state) else { return nil }
            let readings = [ring.short, ring.long].compactMap { window in
                window.map { "\(WindowLabels.label(for: $0)) \(Int(($0.usedRatio * 100).rounded()))%" }
            }
            return "\(state.tool.displayName)  \(readings.joined(separator: " · "))"
        }.joined(separator: "\n")
    }

    public var level: UsageLevel? { highestUsedRatio.map(UsageLevel.init(ratio:)) }

    /// SF Symbols whose needle tracks the level; monochrome, as menu bar items should be.
    public var symbolName: String {
        switch level {
        case nil: "gauge.with.dots.needle.0percent"
        case .ok: "gauge.with.dots.needle.33percent"
        case .caution: "gauge.with.dots.needle.67percent"
        case .danger: "gauge.with.dots.needle.100percent"
        }
    }

    /// One labelled percent per arc
    /// ("5H 32% | W 15%"); the long arc reads "M" for a billing cycle, which is monthly.
    public func percentText(basis: DisplayBasis) -> String? {
        if let ring {
            let parts = [("5H", ring.short), (ring.long?.type == .billingCycle ? "M" : "W", ring.long)]
                .compactMap { label, window in
                    window.map { "\(label) \(basis.percent(for: $0.usedRatio))%" }
                }
            return parts.isEmpty ? nil : parts.joined(separator: " | ")
        }
        return highestUsedRatio.map { "\(basis.percent(for: $0))%" }
    }
}

/// The menu bar target: empty for overall, otherwise a tool's raw value. Earlier builds
/// stored one window ("codex:weekly"); the ring shows a tool's windows together, so such a
/// value is read as its tool.
public enum StatusTarget {
    public static func normalize(_ key: String?) -> String {
        tool(from: key)?.rawValue ?? (key.map { String($0.split(separator: ":", maxSplits: 1).first ?? "") } ?? "")
    }

    public static func tool(from key: String?) -> ToolKind? {
        guard let key, !key.isEmpty, key.caseInsensitiveCompare("all") != .orderedSame else { return nil }
        let toolPart = String(key.split(separator: ":", maxSplits: 1).first ?? "")
        return ToolKind.allCases.first {
            $0.rawValue.caseInsensitiveCompare(toolPart) == .orderedSame ||
                $0.displayName.caseInsensitiveCompare(toolPart) == .orderedSame
        }
    }
}

/// A tool's logo ring: the top arc is the short (5-hour) window, the bottom arc the long one
/// (weekly, or a billing cycle). A nil window leaves that arc as a bare track.
public struct StatusRing: Sendable, Equatable {
    public let tool: ToolKind
    public let short: UsageWindow?
    public let long: UsageWindow?

    public init(tool: ToolKind, short: UsageWindow?, long: UsageWindow?) {
        self.tool = tool
        self.short = short
        self.long = long
    }

    /// The tool's ring, or nil when it has no snapshot or no window the ring can draw.
    public init?(state: ToolState) {
        guard let snapshot = state.snapshot else { return nil }
        let (short, long) = StatusRingSelection.select(snapshot.windows)
        guard short != nil || long != nil else { return nil }
        self.init(tool: state.tool, short: short, long: long)
    }

    /// The higher of the two arcs' used shares.
    var highestUsedRatio: Double {
        [short?.usedRatio, long?.usedRatio].compactMap { $0 }.max() ?? 0
    }
}

extension Array where Element == StatusRing {
    /// The ring that has used the most; the first of equals wins, so the icon does not flip
    /// between tools on every refresh.
    var mostUsed: StatusRing? {
        reduce(nil) { best, ring in
            guard let best else { return ring }
            return ring.highestUsedRatio > best.highestUsedRatio ? ring : best
        }
    }
}

public enum StatusRingSelection {
    /// Both arcs come from the tool's primary family: account-wide windows when it reports
    /// any, otherwise the first family with a 5-hour or weekly window (Antigravity reports
    /// only family-scoped ones). Scoped extras — Fable, Gemini 4 Argon, Codex's named limits
    /// — never reach the ring. A tool billed per cycle (Cursor, Copilot) puts its most-used
    /// billing-cycle window on the long arc. Mirrors the Windows `StatusRingSelection`.
    public static func select(_ windows: [UsageWindow]) -> (short: UsageWindow?, long: UsageWindow?) {
        let rolling = windows.filter { ($0.type == .fiveHour || $0.type == .weekly) && $0.usedRatio.isFinite }
        if let first = rolling.first {
            let family = rolling.contains { ProviderText.normalize($0.groupLabel) == nil }
                ? nil
                : ProviderText.normalize(first.groupLabel)
            let members = rolling.filter { ProviderText.normalize($0.groupLabel) == family }
            return (members.first { $0.type == .fiveHour }, members.first { $0.type == .weekly })
        }
        let billing = windows
            .filter { $0.type == .billingCycle && $0.usedRatio.isFinite }
            .max { $0.usedRatio < $1.usedRatio }
        return (nil, billing)
    }
}
