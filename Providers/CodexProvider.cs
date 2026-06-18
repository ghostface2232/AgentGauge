using System.Diagnostics;
using System.Text.Json;
using Gauge.Models;
using Gauge.Providers.Internal;
using Gauge.Services;

namespace Gauge.Providers;

/// <summary>
/// Reads Codex usage via ccusage.
///
/// LIMITATION: ccusage (v20) does not provide a 5-hour/blocks report for Codex, and
/// "ccusage codex weekly" is explicitly unsupported ("Use ccusage codex daily").
/// So Codex exposes <b>weekly only</b>, derived by aggregating <c>ccusage codex daily
/// --json</c> into weeks (Monday-start). The 5-hour window is intentionally omitted
/// because it cannot be obtained cleanly for Codex.
///
/// As with Claude, there is no quota in the data, so the weekly ratio is an estimate
/// normalized against the busiest week.
/// </summary>
public sealed class CodexProvider : IUsageProvider
{
    private readonly CcusageClient _ccusage;

    public CodexProvider(CcusageClient ccusage) => _ccusage = ccusage;

    public string ToolName => "Codex";

    public async Task<UsageSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        var windows = new List<UsageWindow>();

        // No 5-hour window for Codex (see class remarks).
        try
        {
            if (await GetWeeklyWindowAsync(cancellationToken) is { } weekly)
            {
                windows.Add(weekly);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Debug.WriteLine($"[Gauge] CodexProvider weekly window failed: {ex.Message}");
        }

        return new UsageSnapshot
        {
            ToolName = ToolName,
            Windows = windows,
            CapturedAt = DateTimeOffset.Now,
        };
    }

    private async Task<UsageWindow?> GetWeeklyWindowAsync(CancellationToken cancellationToken)
    {
        var json = await _ccusage.RunAsync("codex daily --json", cancellationToken: cancellationToken);
        using var document = JsonDocument.Parse(json);

        if (!document.RootElement.TryGetArray("daily", out var days))
        {
            return null;
        }

        // Aggregate daily totals into Monday-anchored weeks.
        var weekTotals = new Dictionary<DateOnly, long>();
        foreach (var day in days.EnumerateArray())
        {
            if (day.GetDateOnlyOrNull("date") is not { } date)
            {
                continue;
            }

            var weekStart = WeekMath.MondayOf(date);
            weekTotals[weekStart] = weekTotals.GetValueOrDefault(weekStart) + day.GetLongOrDefault("totalTokens");
        }

        if (weekTotals.Count == 0)
        {
            return null;
        }

        // Use the week that contains "now" (0 if Codex was unused this week) so the
        // reset time is in the future, rather than the latest week that has data.
        var currentWeekStart = WeekMath.CurrentWeekStart();
        var maxWeekTokens = weekTotals.Values.Max();
        var used = weekTotals.GetValueOrDefault(currentWeekStart);

        return new UsageWindow
        {
            Type = UsageWindowType.Weekly,
            UsedRatio = maxWeekTokens > 0 ? Math.Clamp((double)used / maxWeekTokens, 0.0, 1.0) : 0.0,
            Label = "주간",
            ResetTime = new DateTimeOffset(currentWeekStart.AddDays(7).ToDateTime(TimeOnly.MinValue)),
            UsedTokens = used,
            LimitTokens = maxWeekTokens > 0 ? maxWeekTokens : null,
        };
    }
}
