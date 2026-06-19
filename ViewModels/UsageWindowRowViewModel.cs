using CommunityToolkit.Mvvm.ComponentModel;
using Gauge.Localization;
using Gauge.Models;

namespace Gauge.ViewModels;

/// <summary>
/// One usage window row within a tool card (e.g. the 5-hour or weekly bar). A card
/// shows one of these per window the tool actually has.
/// </summary>
public sealed partial class UsageWindowRowViewModel : ObservableObject
{
    public UsageWindowRowViewModel(UsageWindow window)
    {
        Type = window.Type;
        Label = window.Label;
        PercentText = string.Empty;
        ResetText = string.Empty;
        Update(window);
    }

    /// <summary>Stable key used to reconcile rows across refreshes.</summary>
    public UsageWindowType Type { get; }

    /// <summary>Window label (e.g. "5시간", "주간").</summary>
    public string Label { get; }

    /// <summary>0–100 for the progress bar.</summary>
    [ObservableProperty]
    public partial double Percent { get; set; }

    [ObservableProperty]
    public partial string PercentText { get; set; }

    [ObservableProperty]
    public partial string ResetText { get; set; }

    [ObservableProperty]
    public partial UsageLevel Level { get; set; }

    public void Update(UsageWindow window)
    {
        Percent = Math.Clamp(window.UsedRatio, 0.0, 1.0) * 100.0;
        PercentText = $"{window.UsedRatio * 100:0}%";
        Level = UsageLevelClassifier.Classify(window.UsedRatio);
        ResetText = ResetTimeFormatter.ForRow(window.ResetTime);
    }
}
