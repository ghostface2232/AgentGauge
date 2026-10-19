import Foundation

/// Localized "time until reset" phrasing. A row and a notification differ at the edges (an
/// elapsed reset reads "Reset" on a row but "Resetting soon" in a notification; an unknown
/// one is blank on a row but spelled out in a notification); the day/hour/minute middle is shared.
public enum ResetTimeFormatter {
    public static func row(_ reset: Date?, now: Date = Date(), calendar: Calendar = .current) -> String {
        guard let reset else { return "" }
        let remaining = reset.timeIntervalSince(now)
        if remaining <= 0 { return L10n.get("Reset_Done") }
        if remaining >= 24 * 3600 {
            // Whole calendar days to the local reset date, so "N days" agrees with the shown date.
            let days = calendar.dateComponents(
                [.day], from: calendar.startOfDay(for: now), to: calendar.startOfDay(for: reset)).day ?? 1
            return daysPhrase(max(days, 1), reset, calendar: calendar)
        }
        let hours = Int(remaining / 3600)
        let minutes = Int(remaining.truncatingRemainder(dividingBy: 3600) / 60)
        if hours >= 1 { return L10n.format("Reset_InHoursMinutes", hours, minutes) }
        return L10n.format("Reset_InMinutes", minutes)
    }

    public static func notification(_ reset: Date?, now: Date, calendar: Calendar = .current) -> String {
        guard let reset else { return L10n.get("Reset_Unknown") }
        let remaining = reset.timeIntervalSince(now)
        if remaining <= 0 { return L10n.get("Reset_Soon") }
        if remaining >= 24 * 3600 {
            return daysPhrase(max(1, Int((remaining / 86_400).rounded(.up))), reset, calendar: calendar)
        }
        let hours = Int(remaining / 3600)
        let minutes = Int(remaining.truncatingRemainder(dividingBy: 3600) / 60)
        if hours > 0 && minutes > 0 { return L10n.format("Reset_InHoursMinutes", hours, minutes) }
        if hours > 0 { return L10n.format("Reset_InHours", hours) }
        return L10n.format("Reset_InMinutes", max(1, minutes))
    }

    private static func daysPhrase(_ days: Int, _ reset: Date, calendar: Calendar) -> String {
        let formatter = DateFormatter()
        formatter.locale = L10n.locale
        formatter.timeZone = calendar.timeZone
        formatter.dateFormat = L10n.get("DateFormat_MonthDay")
        // English needs "1 day" vs "N days"; Korean/Japanese use one form for both.
        return L10n.format(days == 1 ? "Reset_InDay" : "Reset_InDays", days, formatter.string(from: reset))
    }
}

/// Compares a window's utilization with an even spend across its own cycle.
public enum PaceClassifier {
    public static let minimumElapsedRatio = 0.03

    /// The pace caption, or nil when there is nothing honest to say: no valid
    /// duration/reset, an expired or future cycle, the first 3% of a cycle (too early to
    /// mean anything), or an exhausted window ("burning fast" beside 100% only restates
    /// the bar). Deliberately not coloured — the bar's level tint is the single severity signal.
    public static func caption(for window: UsageWindow, now: Date = Date()) -> String? {
        guard let duration = window.duration, duration > 0,
              let reset = window.resetTime, window.usedRatio.isFinite else { return nil }
        let remaining = reset.timeIntervalSince(now)
        guard remaining > 0, remaining <= duration else { return nil }
        let elapsedRatio = (duration - remaining) / duration
        guard elapsedRatio >= minimumElapsedRatio, window.usedRatio < 1 else { return nil }

        // Positive is reserve, negative deficit, in percentage points of the whole quota.
        let ahead = min(max(window.usedRatio, 0), 1) - elapsedRatio
        let difference = Int((-ahead * 100).rounded(.toNearestOrAwayFromZero))
        if difference == 0 { return L10n.get("Pace_OnPace") }
        return L10n.format(difference > 0 ? "Pace_Reserve" : "Pace_Deficit", abs(difference))
    }
}

/// Notification titles name the window by its own label and scope, so account-wide and
/// model-scoped windows of one type never produce indistinguishable alerts.
public enum NotificationText {
    public static func thresholdTitle(tool: ToolKind, window: UsageWindow, percent: Int) -> String {
        let label = WindowLabels.label(for: window)
        if let group = ProviderText.normalize(window.groupLabel) {
            return L10n.format("Notif_ThresholdScopedTitle", tool.displayName, group, label, percent)
        }
        return L10n.format("Notif_ThresholdTitle", tool.displayName, label, percent)
    }

    public static func resetTitle(tool: ToolKind, window: UsageWindow) -> String {
        let label = WindowLabels.label(for: window)
        if let group = ProviderText.normalize(window.groupLabel) {
            return L10n.format("Notif_ResetScopedTitle", tool.displayName, group, label)
        }
        return L10n.format("Notif_ResetTitle", tool.displayName, label)
    }

    public static func resetMessage(availablePercent: Double) -> String {
        L10n.format("Notif_ResetMessage", Int(availablePercent.rounded()))
    }
}
