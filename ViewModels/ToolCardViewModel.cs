using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Gauge.Localization;
using Gauge.Models;

namespace Gauge.ViewModels;

/// <summary>
/// One tool's card. Shows a row per usage window the tool actually has (5-hour and/or
/// weekly) together — there is no view switch. If the tool has no windows at all
/// (failed or never used), <see cref="HasAnyData"/> is false and the card shows
/// <see cref="StatusText"/> instead.
/// </summary>
public sealed partial class ToolCardViewModel : ObservableObject
{
    public ToolCardViewModel(CachedUsage cached)
    {
        ToolName = cached.ToolName;
        StatusText = string.Empty;
        Plan = string.Empty;
        Update(cached);
    }

    /// <summary>Stable across updates; used to reconcile cards.</summary>
    public string ToolName { get; }

    /// <summary>One row per window the tool exposes, in provider order.</summary>
    public ObservableCollection<UsageWindowRowViewModel> Windows { get; } = new();

    /// <summary>Plan/subscription label shown beside the tool name (e.g. "Max 5x").</summary>
    [ObservableProperty]
    public partial string Plan { get; set; }

    /// <summary>True when a plan label is available (controls its visibility).</summary>
    [ObservableProperty]
    public partial bool HasPlan { get; set; }

    [ObservableProperty]
    public partial bool HasAnyData { get; set; }

    /// <summary>Shown instead of rows when the tool has no windows.</summary>
    [ObservableProperty]
    public partial string StatusText { get; set; }

    public void Update(CachedUsage cached)
    {
        // Plan comes from the snapshot (retained across failed refreshes), so it stays
        // visible even when the current window data is unavailable.
        var plan = cached.Snapshot?.Plan;
        Plan = plan ?? string.Empty;
        HasPlan = !string.IsNullOrEmpty(plan);

        var windows = OrderForDisplay(cached.Snapshot?.Windows ?? Array.Empty<UsageWindow>());

        if (windows.Count == 0)
        {
            HasAnyData = false;
            StatusText = Loc.Get("NoData");
            Windows.Clear();
            return;
        }

        HasAnyData = true;
        StatusText = string.Empty;

        for (var i = Windows.Count - 1; i >= 0; i--)
        {
            if (!windows.Any(w => w.Key == Windows[i].Key))
            {
                Windows.RemoveAt(i);
            }
        }

        // Add new / update existing rows in place, in display order.
        for (var index = 0; index < windows.Count; index++)
        {
            var window = windows[index];
            var existing = Windows.FirstOrDefault(r => r.Key == window.Key);
            if (existing is null)
            {
                Windows.Insert(Math.Min(index, Windows.Count), new UsageWindowRowViewModel(window));
            }
            else
            {
                existing.Update(window);
            }
        }

        AssignGroupHeaders(windows);
    }

    /// <summary>
    /// Orders windows for display when a tool groups them (Antigravity): families stay together
    /// in first-seen order, and within a family the 5-hour limit comes before the weekly one.
    /// Tools without groups keep their provider order unchanged.
    /// </summary>
    private static IReadOnlyList<UsageWindow> OrderForDisplay(IReadOnlyList<UsageWindow> windows)
    {
        if (!windows.Any(w => !string.IsNullOrEmpty(w.GroupLabel)))
        {
            return windows;
        }

        var groupOrder = new Dictionary<string, int>();
        foreach (var window in windows)
        {
            var group = window.GroupLabel ?? string.Empty;
            if (!groupOrder.ContainsKey(group))
            {
                groupOrder[group] = groupOrder.Count;
            }
        }

        return windows
            .Select((window, index) => (window, index))
            .OrderBy(item => groupOrder[item.window.GroupLabel ?? string.Empty])
            .ThenBy(item => TypeRank(item.window.Type))
            .ThenBy(item => item.index)
            .Select(item => item.window)
            .ToList();
    }

    private static int TypeRank(UsageWindowType type) => type switch
    {
        UsageWindowType.FiveHour => 0,
        UsageWindowType.Weekly => 1,
        UsageWindowType.ModelQuota => 2,
        UsageWindowType.BillingCycle => 3,
        _ => 9,
    };

    // The group heading sits on the first row of each family; clear it on the others.
    private void AssignGroupHeaders(IReadOnlyList<UsageWindow> ordered)
    {
        var headed = new HashSet<string>();
        foreach (var window in ordered)
        {
            if (Windows.FirstOrDefault(r => r.Key == window.Key) is not { } row)
            {
                continue;
            }

            row.GroupHeader = window.GroupLabel is { Length: > 0 } group && headed.Add(group)
                ? group
                : string.Empty;
        }
    }
}
