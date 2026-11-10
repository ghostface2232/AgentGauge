import Foundation

/// The default account keeps the original tool key in preferences and the history database.
public struct UsageAccountKey: Hashable, Sendable {
    public let tool: ToolKind
    public let profileID: UUID?

    public init(tool: ToolKind, profileID: UUID? = nil) {
        self.tool = tool
        self.profileID = profileID
    }

    public var storageKey: String {
        profileID.map { "\(tool.rawValue)#\($0.uuidString.lowercased())" } ?? tool.rawValue
    }
}

/// Local configuration for an additional account. The official CLI owns its credentials.
public struct ToolAccount: Identifiable, Codable, Sendable, Equatable {
    public let id: UUID
    public let tool: ToolKind
    public let label: String

    public init(id: UUID = UUID(), tool: ToolKind, label: String) {
        self.id = id
        self.tool = tool
        self.label = label
    }

    public var key: UsageAccountKey { UsageAccountKey(tool: tool, profileID: id) }
    public var displayName: String { "\(tool.displayName) · \(label)" }

    public static func supports(_ tool: ToolKind) -> Bool {
        tool == .claude || tool == .codex || tool == .githubCopilot
    }

    public static func validatedLabel(_ label: String, for tool: ToolKind) -> String? {
        let trimmed = label.trimmingCharacters(in: .whitespacesAndNewlines)
        guard supports(tool), !trimmed.isEmpty, trimmed.count <= 40,
              trimmed.rangeOfCharacter(from: .controlCharacters) == nil else { return nil }
        if tool == .githubCopilot,
           trimmed.range(of: #"\A[A-Za-z0-9][A-Za-z0-9-]*\z"#, options: .regularExpression) == nil { return nil }
        return trimmed
    }
}
