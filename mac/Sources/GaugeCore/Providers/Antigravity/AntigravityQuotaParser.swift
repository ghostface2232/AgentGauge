import Foundation

/// Tolerant parser for the language server's `RetrieveUserQuotaSummary` response. Quota comes
/// as groups (model families) of buckets (one per window); this flattens whatever enabled
/// families are currently offered into `UsageWindow`s, using each bucket's stable `bucketId`
/// as the window's `providerID` and its `window` field ("5h"/"weekly") as the type. It is
/// intentionally dynamic: plans add or withdraw families independently of Gemini.
///
/// Observed live (2026-10, app 2.24.0): "Gemini Models" and "Claude and GPT models", each with
/// `*-weekly` and `*-5h` buckets, plus a model-scoped "Gemini 4 Argon" group with a single
/// weekly bucket (`gemini-argon-wtu-weekly`).
///
/// This is an unstable internal API, so every field is optional: a bucket missing a stable id,
/// a recognized window, or a usable remaining fraction is skipped rather than guessed at, and
/// a bucket is never assumed spent because its usage is absent. Only unparseable JSON throws —
/// a provider failure that keeps the last good snapshot.
enum AntigravityQuotaParser {
    static func windows(from data: Data) throws -> [UsageWindow] {
        windows(from: try JSONObject.parse(data))
    }

    static func windows(from root: JSONObject) -> [UsageWindow] {
        // The payload arrives under "response"; tolerate a "summary" alias and a bare root that
        // already carries "groups", the kind of drift seen across engine builds.
        let payload = root.object("response") ?? root.object("summary") ?? root
        guard let groups = payload.objects("groups") else { return [] }

        var windows: [UsageWindow] = []
        for group in groups where !isUnavailable(group) {
            for bucket in group.objects("buckets") ?? [] {
                if let window = window(bucket, groupName: group.string("displayName")) {
                    windows.append(window)
                }
            }
        }
        return windows
    }

    private static func window(_ bucket: JSONObject, groupName: String?) -> UsageWindow? {
        // bucketId is the provider-stable identity; without it the window cannot be reconciled
        // across refreshes or kept apart from its sibling family's window of the same type.
        guard let bucketID = ProviderText.normalize(bucket.string("bucketId")),
              !isUnavailable(bucket),
              let type = windowType(bucket.string("window")),
              let remaining = remainingFraction(bucket) else { return nil }
        return UsageWindow(
            type: type,
            providerID: bucketID,
            groupLabel: familyLabel(bucketID: bucketID, groupName: groupName),
            usedRatio: min(max(1 - remaining, 0), 1),
            resetTime: resetTime(bucket),
            duration: type == .fiveHour ? 5 * 3600 : 7 * 86400)
    }

    /// The two shared families get short language-neutral names; any other group (a
    /// model-scoped limit such as "Gemini 4 Argon") keeps its own display name, so it never
    /// collapses into "Gemini" and reads as a second, indistinguishable Gemini weekly window.
    /// The observed `gemini-*` / `3p-*` bucket prefixes stand in when a group has no name.
    static func familyLabel(bucketID: String, groupName: String?) -> String? {
        if let name = ProviderText.normalize(groupName) {
            let lowered = name.lowercased()
            if lowered.contains("claude") || lowered.contains("gpt") { return "Claude/GPT" }
            if lowered == "gemini" || lowered == "gemini models" { return "Gemini" }
            return name
        }
        let id = bucketID.lowercased()
        if id.hasPrefix("gemini") { return "Gemini" }
        if id.hasPrefix("3p") { return "Claude/GPT" }
        return nil
    }

    /// A withdrawn/disabled group or bucket carries no live limit — omit it rather than render
    /// it as 0% used. Several spellings are tolerated across engine builds.
    private static func isUnavailable(_ object: JSONObject) -> Bool {
        object.bool("disabled") == true
            || object.bool("isDisabled") == true
            || object.bool("enabled") == false
            || object.bool("available") == false
    }

    private static func windowType(_ window: String?) -> UsageWindowType? {
        switch window {
        case "5h": .fiveHour
        case "weekly": .weekly
        default: nil
        }
    }

    /// A direct float in the observed build, but the field is a protobuf oneof, so the wrapped
    /// forms (`{remainingFraction}` / `{case, value}`) are tolerated too.
    private static func remainingFraction(_ bucket: JSONObject) -> Double? {
        if let direct = bucket.double("remainingFraction") { return direct }
        guard let remaining = bucket.object("remaining") else { return nil }
        if let nested = remaining.double("remainingFraction") { return nested }
        if remaining.string("case") == "remainingFraction" { return remaining.double("value") }
        return nil
    }

    /// ISO-8601 (the observed form) first; a bare number is tolerated as epoch seconds.
    private static func resetTime(_ bucket: JSONObject) -> Date? {
        if let date = bucket.date("resetTime") { return date }
        if bucket.string("resetTime") == nil, let epoch = bucket.int64("resetTime") {
            return Date(timeIntervalSince1970: TimeInterval(epoch))
        }
        return nil
    }
}

/// Reads the plan label from a `GetUserStatus` response: the short plan name ("Pro") at
/// `userStatus.planStatus.planInfo.planName`, else the tier name ("Google AI Pro") at
/// `userStatus.userTier.name`. Best effort: nil for anything missing or unparseable, never
/// an error — the plan never blocks the usage read.
enum AntigravityUserStatus {
    static func plan(from data: Data) -> String? {
        guard let root = try? JSONObject.parse(data) else { return nil }
        guard let status = (root.object("response") ?? root).object("userStatus") else { return nil }
        return ProviderText.normalize(status.object("planStatus")?.object("planInfo")?.string("planName"))
            ?? ProviderText.normalize(status.object("userTier")?.string("name"))
    }
}
