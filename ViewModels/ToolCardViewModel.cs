using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Gauge.Localization;
using Gauge.Models;
using Gauge.Services;

namespace Gauge.ViewModels;

/// <summary>
/// One tool's card. Shows a row per usage window the tool actually has (5-hour and/or
/// weekly) together — there is no view switch. If the tool has no windows at all
/// (failed or never used), <see cref="HasAnyData"/> is false and the card shows
/// <see cref="StatusText"/> instead.
/// </summary>
public sealed partial class ToolCardViewModel : ObservableObject
{
    /// <summary>Recent recorded readings for the ETA projection; null in tests / when absent.</summary>
    private readonly IUsageHistorySource? _history;

    public ToolCardViewModel(CachedUsage cached, IUsageHistorySource? history = null)
    {
        _history = history;
        ToolName = cached.ToolName;
        StatusText = string.Empty;
        Plan = string.Empty;
        Update(cached);
    }

    /// <summary>Stable across updates; used to reconcile cards.</summary>
    public string ToolName { get; }

    /// <summary>One row per window the tool exposes, in provider order.</summary>
    public ObservableCollection<UsageWindowRowViewModel> Windows { get; } = new();

    /// <summary>
    /// The same windows grouped into family rows for the gauge layout (so a divider can sit
    /// between families). Each group's rows are shared instances from <see cref="Windows"/>.
    /// </summary>
    public ObservableCollection<GaugeGroupViewModel> GaugeGroups { get; } = new();

    /// <summary>
    /// How this card renders its windows (bar vs gauge). App-wide; set by the owning
    /// <see cref="UsageViewModel"/> on construction and whenever the user changes the
    /// setting. The card template toggles its bar/gauge lists off <see cref="IsBarMode"/> /
    /// <see cref="IsGaugeMode"/>.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBarMode))]
    [NotifyPropertyChangedFor(nameof(IsGaugeMode))]
    public partial UsageViewMode ViewMode { get; set; }

    public bool IsBarMode => ViewMode == UsageViewMode.Bar;
    public bool IsGaugeMode => ViewMode == UsageViewMode.Gauge;

    /// <summary>Plan/subscription label shown beside the tool name (e.g. "Max 5x").</summary>
    [ObservableProperty]
    public partial string Plan { get; set; }

    /// <summary>True when a plan label is available (controls its visibility).</summary>
    [ObservableProperty]
    public partial bool HasPlan { get; set; }

    [ObservableProperty]
    public partial bool HasAnyData { get; set; }

    /// <summary>
    /// True when this card is showing its last good snapshot because the latest refresh
    /// attempt failed. The usage view represents this with one small status dot; healthy
    /// cards stay visually unchanged.
    /// </summary>
    [ObservableProperty]
    public partial bool HasRefreshIssue { get; set; }

    /// <summary>
    /// True while a refresh attempt covering this tool is in flight (set from the
    /// coordinator's RefreshStarted, cleared by its matching RefreshCompleted). The card
    /// header shows a small indeterminate bar for it, so stale last-good data visibly
    /// has a retry running rather than looking abandoned.
    /// </summary>
    [ObservableProperty]
    public partial bool IsRefreshing { get; set; }

    /// <summary>Shown instead of rows when the tool has no windows.</summary>
    [ObservableProperty]
    public partial string StatusText { get; set; }

    public void Update(CachedUsage cached)
    {
        HasRefreshIssue = cached.LastRefreshFailed;

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
            GaugeGroups.Clear();
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
                existing = new UsageWindowRowViewModel(window);
                Windows.Insert(Math.Min(index, Windows.Count), existing);
            }
            else
            {
                existing.Update(window);
            }
            // ETA from the recorded burn rate. GetRecent is memory-backed after its first
            // read, so this stays cheap on the UI thread.
            existing.EtaText = _history is null
                ? string.Empty
                : UsageEtaClassifier.ForRow(
                    window, _history.GetRecent(ToolName, window.Key, UsageEtaClassifier.Lookback));
        }

        AssignGroupHeaders(windows);
        RebuildGaugeGroups(windows);
    }

    /// <summary>Re-runs the level-to-brush bindings on every row after a live theme
    /// change (see <see cref="UsageWindowRowViewModel.RefreshLevelBrushes"/>).</summary>
    public void RefreshLevelBrushes()
    {
        foreach (var row in Windows)
        {
            row.RefreshLevelBrushes();
        }
    }

    /// <summary>
    /// Orders windows for display when a tool groups them by model/family scope: groups stay
    /// together in first-seen order, and within a group 5-hour comes before weekly.
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

    // The group heading sits on the first row of each family; clear it on the others. A divider
    // is drawn above every group's first row except the first, separating adjacent families.
    private void AssignGroupHeaders(IReadOnlyList<UsageWindow> ordered)
    {
        var headed = new HashSet<string>();
        for (var index = 0; index < ordered.Count; index++)
        {
            var window = ordered[index];
            if (Windows.FirstOrDefault(r => r.Key == window.Key) is not { } row)
            {
                continue;
            }

            if (window.GroupLabel is { Length: > 0 } group && headed.Add(group))
            {
                row.GroupHeader = group;
                // A named scope can follow account-wide ungrouped windows (Claude Fable /
                // Codex additional limits), so position — not named-group count — decides
                // whether a visual divider is needed.
                row.ShowGroupDivider = index > 0;
            }
            else
            {
                row.GroupHeader = string.Empty;
                row.ShowGroupDivider = false;
            }
        }
    }

    // Groups the (already display-ordered) windows into family rows for the gauge layout:
    // one group per family for grouped tools, a single group for ungrouped ones. The group
    // and row containers are reconciled in place (membership is stable across refreshes), and
    // the rows are the same instances as Windows, so their values update without a rebuild.
    private void RebuildGaugeGroups(IReadOnlyList<UsageWindow> ordered)
    {
        var grouped = ordered.Any(w => !string.IsNullOrEmpty(w.GroupLabel));

        var keysInOrder = new List<string>();
        var rowsByKey = new Dictionary<string, List<UsageWindowRowViewModel>>();
        foreach (var window in ordered)
        {
            var key = grouped ? window.GroupLabel ?? string.Empty : string.Empty;
            if (!rowsByKey.TryGetValue(key, out var rows))
            {
                rows = new List<UsageWindowRowViewModel>();
                rowsByKey[key] = rows;
                keysInOrder.Add(key);
            }
            if (Windows.FirstOrDefault(r => r.Key == window.Key) is { } row)
            {
                rows.Add(row);
            }
        }

        for (var i = GaugeGroups.Count - 1; i >= 0; i--)
        {
            if (!keysInOrder.Contains(GaugeGroups[i].Key))
            {
                GaugeGroups.RemoveAt(i);
            }
        }

        for (var index = 0; index < keysInOrder.Count; index++)
        {
            var key = keysInOrder[index];
            var group = GaugeGroups.FirstOrDefault(g => g.Key == key);
            if (group is null)
            {
                group = new GaugeGroupViewModel(key);
                GaugeGroups.Insert(Math.Min(index, GaugeGroups.Count), group);
            }
            group.ShowDivider = index > 0;
            ReconcileRows(group.Rows, rowsByKey[key]);
        }
    }

    private static void ReconcileRows(
        ObservableCollection<UsageWindowRowViewModel> target, IReadOnlyList<UsageWindowRowViewModel> source)
    {
        for (var i = target.Count - 1; i >= 0; i--)
        {
            if (!source.Contains(target[i]))
            {
                target.RemoveAt(i);
            }
        }

        for (var index = 0; index < source.Count; index++)
        {
            if (index >= target.Count || !ReferenceEquals(target[index], source[index]))
            {
                target.Remove(source[index]);
                target.Insert(Math.Min(index, target.Count), source[index]);
            }
        }
    }
}
