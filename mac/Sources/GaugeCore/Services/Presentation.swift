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

/// What the menu bar item shows: the most-used window across shown tools, so the state is
/// glanceable without opening the panel.
public struct MenuBarSummary: Sendable, Equatable {
    public let highestUsedRatio: Double?

    public init(states: [ToolState]) {
        highestUsedRatio = states
            .flatMap { $0.snapshot?.windows ?? [] }
            .map(\.usedRatio)
            .filter(\.isFinite)
            .max()
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

    public func percentText(basis: DisplayBasis) -> String? {
        highestUsedRatio.map { "\(basis.percent(for: $0))%" }
    }
}
