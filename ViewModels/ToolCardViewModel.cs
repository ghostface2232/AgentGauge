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

        var windows = cached.Snapshot?.Windows ?? (IReadOnlyList<UsageWindow>)Array.Empty<UsageWindow>();

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

        // Add new / update existing rows in place, preserving provider order.
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
    }
}
