using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Gauge.Localization;
using Gauge.Models;
using Gauge.Services;

namespace Gauge.ViewModels;

/// <summary>
/// UI-facing view model for the popover. The coordinator pushes a
/// <see cref="UsageState"/> via <see cref="Apply"/> (on the UI thread). Each tool gets
/// one card showing all of its windows (5-hour and/or weekly) at once. The tray
/// tooltip is built from <see cref="TrayTooltipSummary"/>.
/// </summary>
public sealed partial class UsageViewModel : ObservableObject
{
    /// <summary>Source of the display order, shared with the settings screen so a reorder on
    /// either surface shows on the other. Optional so tests can construct the VM standalone
    /// (cards then keep the order the coordinator supplies).</summary>
    private readonly ToolRegistry? _registry;

    public UsageViewModel(ToolRegistry? registry = null)
    {
        _registry = registry;
        // A reorder elsewhere (the settings screen) re-sorts the cards in place — no re-fetch.
        if (_registry is not null)
        {
            _registry.OrderChanged += (_, _) => ResortCards();
        }
        LastUpdatedText = Loc.Get("LastUpdated_Never");
        TrayTooltipSummary = "AgentGauge";
        EmptyMessage = Loc.Get("Loading");
        IsEmpty = true;
        RefreshCommand = new RelayCommand(() => RefreshRequested?.Invoke(this, EventArgs.Empty));
    }

    /// <summary>
    /// Persists a new card order after a drag on the main screen. Maps each card's tool name
    /// back to its <see cref="ToolKind"/> (every provider's <c>ToolName</c> is its catalog
    /// display name) and hands it to the registry, which propagates the order to the settings
    /// screen via its <see cref="ToolRegistry.Changed"/> event.
    /// </summary>
    public void ReorderTools(IReadOnlyList<string> toolNamesInNewOrder)
    {
        if (_registry is null)
        {
            return;
        }
        var kinds = toolNamesInNewOrder
            .Select(name => ToolCatalog.All.FirstOrDefault(d => d.DisplayName == name)?.Kind)
            .OfType<ToolKind>()
            .ToList();
        _registry.ReorderEnabled(kinds);
    }

    /// <summary>Reorders the existing cards in place to match the registry's display order,
    /// without rebuilding containers or re-fetching usage. Called when the order changes on
    /// another screen.</summary>
    public void ResortCards()
    {
        if (_registry is null)
        {
            return;
        }
        var target = 0;
        foreach (var kind in _registry.Enabled)
        {
            var name = ToolCatalog.For(kind).DisplayName;
            for (var i = target; i < Cards.Count; i++)
            {
                if (Cards[i].ToolName == name)
                {
                    if (i != target)
                    {
                        Cards.Move(i, target);
                    }
                    target++;
                    break;
                }
            }
        }
    }

    /// <summary>The display-order position of a tool name, or <see cref="int.MaxValue"/> when
    /// it has no registry slot (no registry, or not registered) so it sorts to the end while
    /// preserving the coordinator's relative order via a stable sort.</summary>
    private int OrderOf(string toolName)
    {
        if (_registry is null)
        {
            return int.MaxValue;
        }
        var enabled = _registry.Enabled;
        for (var i = 0; i < enabled.Count; i++)
        {
            if (ToolCatalog.For(enabled[i]).DisplayName == toolName)
            {
                return i;
            }
        }
        return int.MaxValue;
    }

    /// <summary>One card per tool.</summary>
    public ObservableCollection<ToolCardViewModel> Cards { get; } = new();

    /// <summary>
    /// App-wide card layout (bar vs gauge). Owned here so newly added cards inherit it and
    /// <see cref="SetViewMode"/> can flip every card at once. Not bound directly by the UI —
    /// each card exposes its own bar/gauge visibility off this.
    /// </summary>
    public UsageViewMode ViewMode { get; private set; }

    /// <summary>Switches every card to the given layout (called from the settings dropdown).</summary>
    public void SetViewMode(UsageViewMode mode)
    {
        ViewMode = mode;
        foreach (var card in Cards)
        {
            card.ViewMode = mode;
        }
    }

    /// <summary>Manual refresh button; the app routes this through the debounced coordinator.</summary>
    public IRelayCommand RefreshCommand { get; }

    /// <summary>Raised when the user clicks manual refresh.</summary>
    public event EventHandler? RefreshRequested;

    [ObservableProperty]
    public partial DateTimeOffset? LastUpdatedAt { get; set; }

    [ObservableProperty]
    public partial string LastUpdatedText { get; set; }

    [ObservableProperty]
    public partial string TrayTooltipSummary { get; set; }

    /// <summary>True when no tool has any window data (shows a guidance message).</summary>
    [ObservableProperty]
    public partial bool IsEmpty { get; set; }

    /// <summary>User-facing message shown when <see cref="IsEmpty"/> is true.</summary>
    [ObservableProperty]
    public partial string EmptyMessage { get; set; }

    /// <summary>
    /// Highest usage ratio (0–1) across every tool/window, used to pick the tray
    /// icon variant. 0 when there is no data. Not bound by the UI, so a plain property.
    /// </summary>
    public double HighestUsageRatio { get; private set; }

    public void Apply(UsageState state)
    {
        // The usage surface shows every tool Gauge has a record for — i.e. one that has
        // succeeded at least once (HasData), even if its snapshot is now stale or has no
        // windows (those render as a "no data" card). Tools that have never succeeded (no
        // snapshot: not signed in, no OAuth, no history) are left off, so the default
        // Claude/Codex cards aren't forced onto users who don't use one of them.
        // Order by the shared registry display order so the cards match the settings screen
        // (and reflect a drag-to-reorder done on either surface). OrderBy is stable, so tools
        // without a registry slot keep the coordinator's relative order at the end.
        var recorded = state.Tools.Where(t => t.HasData).OrderBy(t => OrderOf(t.ToolName)).ToList();

        HighestUsageRatio = recorded
            .SelectMany(t => t.Snapshot!.Windows)
            .Select(w => w.UsedRatio)
            .DefaultIfEmpty(0)
            .Max();

        LastUpdatedAt = state.LastUpdatedAt;
        LastUpdatedText = state.LastUpdatedAt is { } updated
            ? Loc.Format("LastUpdated_At", updated.ToLocalTime().ToString("HH:mm"))
            : Loc.Get("LastUpdated_Never");
        TrayTooltipSummary = BuildTrayTooltipSummary(recorded);
        RefreshCards(recorded);

        IsEmpty = recorded.Count == 0;
        if (IsEmpty)
        {
            EmptyMessage = BuildEmptyMessage(state);
        }
    }

    private static string BuildEmptyMessage(UsageState state)
    {
        if (state.Tools.Count == 0)
        {
            return Loc.Get("Loading");
        }

        // All providers errored with no cached value (network or expired login).
        if (state.Tools.All(t => t.LastRefreshFailed && t.Snapshot is null))
        {
            return Loc.Get("Empty_FetchFailed");
        }

        // Providers ran but returned nothing (e.g. tools not used yet).
        return Loc.Get("Empty_NoHistory");
    }

    private void RefreshCards(IReadOnlyList<CachedUsage> tools)
    {
        for (var i = Cards.Count - 1; i >= 0; i--)
        {
            if (!tools.Any(t => t.ToolName == Cards[i].ToolName))
            {
                Cards.RemoveAt(i);
            }
        }

        // Add new tools / update existing in place (avoids container churn).
        foreach (var tool in tools)
        {
            var existing = Cards.FirstOrDefault(c => c.ToolName == tool.ToolName);
            if (existing is null)
            {
                Cards.Add(new ToolCardViewModel(tool) { ViewMode = ViewMode });
            }
            else
            {
                existing.Update(tool);
            }
        }

        // Reorder cards in place to match the incoming (registry-ordered) sequence, so a
        // reorder done elsewhere is reflected here without rebuilding the containers.
        for (var target = 0; target < tools.Count; target++)
        {
            var name = tools[target].ToolName;
            var current = -1;
            for (var i = target; i < Cards.Count; i++)
            {
                if (Cards[i].ToolName == name)
                {
                    current = i;
                    break;
                }
            }
            if (current > target)
            {
                Cards.Move(current, target);
            }
        }
    }

    private static string BuildTrayTooltipSummary(IReadOnlyList<CachedUsage> tools)
    {
        if (tools.Count == 0)
        {
            return "AgentGauge";
        }

        // Compact one-line-per-tool summary using each tool's highest window ratio, e.g.
        // "Claude Code 63% · Codex 42%". A recorded tool with no windows (stale/empty
        // snapshot) shows "<tool> no data" instead. Kept short for the shell tooltip.
        var parts = new List<string>();
        foreach (var tool in tools)
        {
            if (tool.Snapshot is { Windows.Count: > 0 } snapshot)
            {
                var highest = snapshot.Windows.Max(w => w.UsedRatio);
                parts.Add($"{tool.ToolName} {highest * 100:0}%");
            }
            else
            {
                parts.Add(Loc.Format("Tray_NoData", tool.ToolName));
            }
        }

        return string.Join(" · ", parts);
    }
}
