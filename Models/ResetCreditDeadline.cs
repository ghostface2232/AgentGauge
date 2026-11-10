namespace Gauge.Models;

public sealed record ResetCreditDeadline(DateTimeOffset ExpiresAt, string? Scope = null)
{
    public static readonly TimeSpan WarningLeadTime = TimeSpan.FromDays(2);

    public static ResetCreditDeadline? Upcoming(IReadOnlyList<ResetCreditDeadline>? deadlines, DateTimeOffset now)
        => deadlines?.Where(deadline => deadline.ExpiresAt > now && deadline.ExpiresAt - now <= WarningLeadTime)
            .MinBy(deadline => deadline.ExpiresAt);
}
