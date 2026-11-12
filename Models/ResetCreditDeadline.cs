namespace Gauge.Models;

public sealed record ResetCreditDeadline(DateTimeOffset ExpiresAt, string? Scope = null, int? Count = null)
{
    public static readonly TimeSpan WarningLeadTime = TimeSpan.FromDays(2);

    public static ResetCreditDeadline? Upcoming(IReadOnlyList<ResetCreditDeadline>? deadlines, DateTimeOffset now)
        => deadlines?.Where(deadline => deadline.ExpiresAt > now && deadline.ExpiresAt - now <= WarningLeadTime)
            .MinBy(deadline => deadline.ExpiresAt);

    /// <summary>
    /// Held reset count. Codex reports an account-level balance; Claude has none at snapshot
    /// level, so its still-valid grants' counts stand in for it.
    /// </summary>
    public static int AvailableCount(int? balance, IReadOnlyList<ResetCreditDeadline>? deadlines, DateTimeOffset now)
        => balance is { } known
            ? Math.Max(known, 0)
            : (int)Math.Min(deadlines?.Where(d => d.ExpiresAt > now && d.Count is > 0).Sum(d => (long)d.Count!.Value) ?? 0,
                int.MaxValue);

    /// <summary>An expiry crossed since the last account balance was observed.</summary>
    public static bool IsBalanceUnconfirmed(
        int? balance, IReadOnlyList<ResetCreditDeadline>? deadlines, DateTimeOffset capturedAt, DateTimeOffset now)
        => balance is > 0 && deadlines?.Any(deadline => deadline.ExpiresAt > capturedAt && deadline.ExpiresAt <= now) == true;
}
