using Gauge.Models;

namespace Gauge.Localization;

public static class ResetCreditText
{
    public static string Remaining(ResetCreditDeadline deadline, DateTimeOffset now)
    {
        var remaining = deadline.ExpiresAt - now;
        if (remaining > TimeSpan.FromDays(1)) return Loc.Get("ResetCredit_TwoDays");
        if (remaining < TimeSpan.FromHours(1)) return Loc.Get("ResetCredit_UnderHour");
        return Loc.Format("ResetCredit_Hours", (int)Math.Ceiling(remaining.TotalHours));
    }

    public static string Description(ResetCreditDeadline deadline)
        => Loc.Format(deadline.Scope is null ? "ResetCredit_Expiry" : "ResetCredit_ScopedExpiry",
            deadline.ExpiresAt.LocalDateTime.ToString("g", Loc.Culture), deadline.Scope);
}
