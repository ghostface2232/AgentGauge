namespace Gauge.Providers.Internal;

/// <summary>
/// Week math for usage windows. ccusage groups weeks starting on Monday (verified
/// against real output), so we anchor weekly windows to the Monday of the current
/// local week. This keeps "weekly" meaning the in-progress week (resetting in the
/// future), not just the most recent week that happens to have data.
/// </summary>
internal static class WeekMath
{
    public static DateOnly MondayOf(DateOnly date)
    {
        // DayOfWeek: Sunday=0..Saturday=6; shift so Monday is the week start.
        var offset = ((int)date.DayOfWeek + 6) % 7;
        return date.AddDays(-offset);
    }

    public static DateOnly CurrentWeekStart()
        => MondayOf(DateOnly.FromDateTime(DateTime.Now));
}
