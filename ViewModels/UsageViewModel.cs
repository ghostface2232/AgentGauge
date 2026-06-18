using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Gauge.Models;

namespace Gauge.ViewModels;

/// <summary>
/// UI-facing view model for the popover. The coordinator pushes a
/// <see cref="UsageState"/> via <see cref="Apply"/> (on the UI thread). Each tool gets
/// one card showing all of its windows (5-hour and/or weekly) at once. The tray
/// tooltip is built from <see cref="TrayTooltipSummary"/>.
/// </summary>
public sealed partial class UsageViewModel : ObservableObject
{
    public UsageViewModel()
    {
        LastUpdatedText = "갱신 전";
        TrayTooltipSummary = "Gauge";
        EmptyMessage = "사용량을 불러오는 중…";
        IsEmpty = true;
        RefreshCommand = new RelayCommand(() => RefreshRequested?.Invoke(this, EventArgs.Empty));
    }

    /// <summary>One card per tool.</summary>
    public ObservableCollection<ToolCardViewModel> Cards { get; } = new();

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
        HighestUsageRatio = state.Tools
            .Where(t => t.Snapshot is { Windows.Count: > 0 })
            .SelectMany(t => t.Snapshot!.Windows)
            .Select(w => w.UsedRatio)
            .DefaultIfEmpty(0)
            .Max();

        LastUpdatedAt = state.LastUpdatedAt;
        LastUpdatedText = state.LastUpdatedAt is { } updated
            ? $"{updated.ToLocalTime():HH:mm} 갱신"
            : "갱신 전";
        TrayTooltipSummary = BuildTrayTooltipSummary(state);
        RefreshCards(state);

        var anyData = state.Tools.Any(t => t.Snapshot is { Windows.Count: > 0 });
        IsEmpty = !anyData;
        if (!anyData)
        {
            EmptyMessage = BuildEmptyMessage(state);
        }
    }

    private static string BuildEmptyMessage(UsageState state)
    {
        if (state.Tools.Count == 0)
        {
            return "사용량을 불러오는 중…";
        }

        // All providers errored with no cached value (network or expired login).
        if (state.Tools.All(t => t.LastRefreshFailed && t.Snapshot is null))
        {
            return "사용량 정보를 불러올 수 없습니다.\n설정에서 로그인 상태를 확인하세요.";
        }

        // Providers ran but returned nothing (e.g. tools not used yet).
        return "사용 기록이 아직 없습니다.";
    }

    private void RefreshCards(UsageState state)
    {
        var tools = state.Tools;

        // Remove cards for tools no longer present.
        for (var i = Cards.Count - 1; i >= 0; i--)
        {
            if (!tools.Any(t => t.ToolName == Cards[i].ToolName))
            {
                Cards.RemoveAt(i);
            }
        }

        // Add new tools / update existing in place (avoids container churn).
        for (var index = 0; index < tools.Count; index++)
        {
            var tool = tools[index];
            var existing = Cards.FirstOrDefault(c => c.ToolName == tool.ToolName);
            if (existing is null)
            {
                Cards.Insert(Math.Min(index, Cards.Count), new ToolCardViewModel(tool));
            }
            else
            {
                existing.Update(tool);
            }
        }
    }

    private static string BuildTrayTooltipSummary(UsageState state)
    {
        if (state.Tools.Count == 0)
        {
            return "Gauge";
        }

        // Compact one-line-per-tool summary using each tool's highest window ratio,
        // e.g. "Claude Code 63% · Codex 데이터 없음". Kept short for the shell tooltip.
        var parts = new List<string>();
        foreach (var tool in state.Tools)
        {
            if (tool.Snapshot is { Windows.Count: > 0 } snapshot)
            {
                var highest = snapshot.Windows.Max(w => w.UsedRatio);
                parts.Add($"{tool.ToolName} {highest * 100:0}%");
            }
            else
            {
                parts.Add($"{tool.ToolName} 데이터 없음");
            }
        }

        return string.Join(" · ", parts);
    }
}
