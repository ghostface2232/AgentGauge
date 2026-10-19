import Foundation
import os

/// Small text helpers shared by the usage parsers.
enum ProviderText {
    /// Trimmed non-empty text, or nil — whitespace-only reads as absent.
    static func normalize(_ value: String?) -> String? {
        guard let trimmed = value?.trimmingCharacters(in: .whitespacesAndNewlines), !trimmed.isEmpty else { return nil }
        return trimmed
    }

    /// Lowercase letters/digits with single dashes between runs ("GPT-5 Codex" → "gpt-5-codex"),
    /// used to derive stable window ids from provider-reported names.
    static func slug(_ value: String) -> String {
        var result = ""
        var lastWasDash = false
        for character in value.lowercased() {
            if character.isLetter || character.isNumber {
                result.append(character)
                lastWasDash = false
            } else if !lastWasDash && !result.isEmpty {
                result.append("-")
                lastWasDash = true
            }
        }
        while result.hasSuffix("-") { result.removeLast() }
        return result
    }

    /// Known plan values map explicitly; an unknown non-empty value degrades to itself
    /// capitalized rather than being hidden.
    static func planLabel(_ raw: String?, known: [String: String]) -> String? {
        guard let normalized = raw?.lowercased(), !normalized.isEmpty else { return nil }
        return known[normalized] ?? normalized.prefix(1).uppercased() + normalized.dropFirst()
    }
}

/// Diagnostics go to the unified log (Console.app, subsystem below), which persists in
/// release builds. Scrub rule: exception types and short messages only — never tokens,
/// credential-bearing URLs, or CLI output.
public enum Log {
    public static let subsystem = "io.github.ghostface2232.agentgauge"

    /// Test runs exercise the failure paths on purpose; their lines go to a sibling
    /// subsystem so they never read as real failures in the app's own log.
    private static let activeSubsystem = Bundle.allBundles.contains { $0.bundlePath.hasSuffix(".xctest") }
        ? subsystem + ".tests" : subsystem

    public static func write(_ area: String, _ message: String) {
        Logger(subsystem: activeSubsystem, category: area).notice("\(message, privacy: .public)")
    }
}

/// Monotonic time in seconds for cooldowns and cadences, so a wall-clock change can neither
/// stall nor skip them. Injectable so the gates are testable without waiting.
public protocol MonotonicClock: Sendable {
    func now() -> TimeInterval
}

public struct SystemMonotonicClock: MonotonicClock {
    public init() {}

    // CLOCK_MONOTONIC keeps counting while the Mac sleeps (unlike systemUptime), so a
    // cooldown armed before sleep is judged by real elapsed time after wake.
    public func now() -> TimeInterval {
        TimeInterval(clock_gettime_nsec_np(CLOCK_MONOTONIC)) / 1_000_000_000
    }
}
