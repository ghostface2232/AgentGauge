namespace Gauge.Localization;

/// <summary>
/// Localized "time until reset" phrasing, shared by the popover row and the usage
/// notifications. The two surfaces differ at the edges (an already-elapsed reset reads
/// "Reset" on a row but "Resetting soon" in a notification, and an unknown reset time is
/// blank on a row but spelled out in a notification), so each has its own entry point;
/// the in-between day/hour/minute phrasing is shared.
/// </summary>
public static class ResetTimeFormatter
{
    /// <summary>Popover-row phrasing. Empty string when the reset time is unknown.</summary>
    public static string ForRow(DateTimeOffset? reset)
    {
        if (reset is not { } resetAt)
        {
            return string.Empty;
        }

        var remaining = resetAt - DateTimeOffset.Now;
        if (remaining <= TimeSpan.Zero)
        {
            return Loc.Get("Reset_Done");
        }

        if (remaining.TotalHours >= 24)
        {
            // Whole calendar days to the (local) reset date, so "N days" agrees with the
            // shown date instead of rounding elapsed time down.
            var localReset = resetAt.ToLocalTime();
            var dayDiff = (localReset.Date - DateTime.Now.Date).Days;
            return DaysPhrase(dayDiff, localReset);
        }

        if (remaining.TotalHours >= 1)
        {
            return Loc.Format("Reset_InHoursMinutes", (int)remaining.TotalHours, remaining.Minutes);
        }

        return Loc.Format("Reset_InMinutes", remaining.Minutes);
    }

    /// <summary>Notification phrasing. Spells out unknown / imminent resets.</summary>
    public static string ForNotification(DateTimeOffset? reset, DateTimeOffset now)
    {
        if (reset is null)
        {
            return Loc.Get("Reset_Unknown");
        }

        var localReset = reset.Value.ToLocalTime();
        var remaining = reset.Value - now;
        if (remaining <= TimeSpan.Zero)
        {
            return Loc.Get("Reset_Soon");
        }

        if (remaining.TotalDays >= 1)
        {
            var days = Math.Max(1, (int)Math.Ceiling(remaining.TotalDays));
            return DaysPhrase(days, localReset);
        }

        var hours = (int)remaining.TotalHours;
        var minutes = remaining.Minutes;
        if (hours > 0 && minutes > 0) return Loc.Format("Reset_InHoursMinutes", hours, minutes);
        if (hours > 0) return Loc.Format("Reset_InHours", hours);
        return Loc.Format("Reset_InMinutes", Math.Max(1, minutes));
    }

    private static string DaysPhrase(int days, DateTimeOffset localReset)
    {
        // English needs "1 day" vs "N days"; Korean/Japanese use one form for both.
        var key = days == 1 ? "Reset_InDay" : "Reset_InDays";
        return Loc.Format(key, days, localReset.ToString(Loc.Get("DateFormat_MonthDay"), Loc.Culture));
    }
}
