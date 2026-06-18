using System.Diagnostics;
using System.Text.Json;
using Gauge.Models;
using Gauge.Providers.Internal;
using Gauge.Services;

namespace Gauge.Providers;

/// <summary>
/// Reads Claude Code usage via ccusage:
///   - <c>ccusage blocks --json</c> for the active 5-hour window.
///   - <c>ccusage weekly --json</c> for the weekly window (filtered to Claude models,
///     since that command aggregates every detected agent).
///
/// ccusage exposes no quota, so ratios are estimates: the active window is normalized
/// against the largest historical block (mirroring ccusage's own <c>--token-limit max</c>
/// convention) and the weekly window against the busiest week. Accepted for v1.
///
/// Each window is fetched in its own try/catch so one failing call (or missing data)
/// never suppresses the other window.
/// </summary>
public sealed class ClaudeProvider : IUsageProvider
{
    private readonly CcusageClient _ccusage;

    public ClaudeProvider(CcusageClient ccusage) => _ccusage = ccusage;

    public string ToolName => "Claude Code";

    public async Task<UsageSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        var windows = new List<UsageWindow>();

        try
        {
            if (await GetFiveHourWindowAsync(cancellationToken) is { } fiveHour)
            {
                windows.Add(fiveHour);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Debug.WriteLine($"[Gauge] ClaudeProvider 5-hour window failed: {ex.Message}");
        }

        try
        {
            if (await GetWeeklyWindowAsync(cancellationToken) is { } weekly)
            {
                windows.Add(weekly);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Debug.WriteLine($"[Gauge] ClaudeProvider weekly window failed: {ex.Message}");
        }

        return new UsageSnapshot
        {
            ToolName = ToolName,
            Windows = windows,
            CapturedAt = DateTimeOffset.Now,
        };
    }

    private async Task<UsageWindow?> GetFiveHourWindowAsync(CancellationToken cancellationToken)
    {
        var json = await _ccusage.RunAsync("blocks --json", cancellationToken: cancellationToken);
        using var document = JsonDocument.Parse(json);

        if (!document.RootElement.TryGetArray("blocks", out var blocks))
        {
            return null;
        }

        long maxTokens = 0;
        JsonElement? active = null;
        foreach (var block in blocks.EnumerateArray())
        {
            if (block.GetBoolOrDefault("isGap"))
            {
                continue;
            }

            maxTokens = Math.Max(maxTokens, block.GetLongOrDefault("totalTokens"));
            if (block.GetBoolOrDefault("isActive"))
            {
                active = block;
            }
        }

        // No active block means there is no current 5-hour session to show.
        if (active is not { } activeBlock)
        {
            return null;
        }

        var used = activeBlock.GetLongOrDefault("totalTokens");
        return new UsageWindow
        {
            Type = UsageWindowType.FiveHour,
            UsedRatio = Ratio(used, maxTokens),
            Label = "5시간",
            ResetTime = activeBlock.GetDateTimeOffsetOrNull("endTime"),
            UsedTokens = used,
            LimitTokens = maxTokens > 0 ? maxTokens : null,
        };
    }

    private async Task<UsageWindow?> GetWeeklyWindowAsync(CancellationToken cancellationToken)
    {
        var json = await _ccusage.RunAsync("weekly --json", cancellationToken: cancellationToken);
        using var document = JsonDocument.Parse(json);

        if (!document.RootElement.TryGetArray("weekly", out var weeks))
        {
            return null;
        }

        // Anchor to the week that contains "now" so the window represents the
        // in-progress week (0 if Claude was unused this week), not the latest week
        // that has data. ccusage periods are Monday-start week dates.
        var currentWeekStart = WeekMath.CurrentWeekStart();
        long maxClaudeTokens = 0;
        long currentClaudeTokens = 0;
        var hasAnyWeek = false;
        foreach (var week in weeks.EnumerateArray())
        {
            hasAnyWeek = true;
            var claudeTokens = SumClaudeTokens(week);
            maxClaudeTokens = Math.Max(maxClaudeTokens, claudeTokens);

            if (week.GetDateOnlyOrNull("period") == currentWeekStart)
            {
                currentClaudeTokens = claudeTokens;
            }
        }

        if (!hasAnyWeek)
        {
            return null;
        }

        return new UsageWindow
        {
            Type = UsageWindowType.Weekly,
            UsedRatio = Ratio(currentClaudeTokens, maxClaudeTokens),
            Label = "주간",
            // Weekly periods are week-start dates; the window resets 7 days later.
            ResetTime = new DateTimeOffset(currentWeekStart.AddDays(7).ToDateTime(TimeOnly.MinValue)),
            UsedTokens = currentClaudeTokens,
            LimitTokens = maxClaudeTokens > 0 ? maxClaudeTokens : null,
        };
    }

    /// <summary>
    /// Sums tokens for Claude models only within a weekly entry, since
    /// <c>ccusage weekly</c> combines all detected agents (Claude, Codex, …).
    /// </summary>
    private static long SumClaudeTokens(JsonElement week)
    {
        if (!week.TryGetArray("modelBreakdowns", out var breakdowns))
        {
            return 0;
        }

        long total = 0;
        foreach (var model in breakdowns.EnumerateArray())
        {
            var name = model.GetStringOrNull("modelName");
            if (name is null || !name.StartsWith("claude", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // modelBreakdowns has no per-model total, so sum the token components.
            total += model.GetLongOrDefault("inputTokens")
                   + model.GetLongOrDefault("outputTokens")
                   + model.GetLongOrDefault("cacheCreationTokens")
                   + model.GetLongOrDefault("cacheReadTokens");
        }

        return total;
    }

    private static double Ratio(long used, long limit)
        => limit > 0 ? Math.Clamp((double)used / limit, 0.0, 1.0) : 0.0;
}
