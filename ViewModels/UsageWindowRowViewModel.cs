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
        Key = window.Key;
        Label = string.Empty;
        FamilyLabel = string.Empty;
        GroupHeader = string.Empty;
        PercentText = string.Empty;
        PercentNumber = string.Empty;
        ResetText = string.Empty;
        PaceText = string.Empty;
        Update(window);
    }

    /// <summary>Provider-stable key used to reconcile rows across refreshes.</summary>
    public string Key { get; }

    /// <summary>Window label (e.g. "5시간", "주간").</summary>
    [ObservableProperty]
    public partial string Label { get; set; }

    /// <summary>
    /// Model-family name for this window (e.g. "Gemini", "Claude/GPT"), empty for ungrouped
    /// tools. Unlike <see cref="GroupHeader"/> — which the card sets only on the first row of
    /// each group for the bar layout — this is the window's own family and is shown on every
    /// gauge in gauge mode, where each gauge stands alone in a grid cell.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFamilyLabel))]
    public partial string FamilyLabel { get; set; }

    /// <summary>True when this window belongs to a model family (controls the gauge label).</summary>
    public bool HasFamilyLabel => !string.IsNullOrEmpty(FamilyLabel);

    /// <summary>
    /// Family heading shown above this row, set only on the first row of each group (e.g.
    /// "Gemini", "Claude/GPT"); empty otherwise. The card assigns it by display position, so it
    /// is not a property of the window itself.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasGroupHeader))]
    public partial string GroupHeader { get; set; }

    public bool HasGroupHeader => !string.IsNullOrEmpty(GroupHeader);

    /// <summary>
    /// Whether a separator line is drawn above this row's group heading. Set on the first row of
    /// every group except the first, so it sits between adjacent families (e.g. Gemini and
    /// Claude/GPT) rather than above the top one.
    /// </summary>
    [ObservableProperty]
    public partial bool ShowGroupDivider { get; set; }

    /// <summary>0–100 for the progress bar.</summary>
    [ObservableProperty]
    public partial double Percent { get; set; }

    [ObservableProperty]
    public partial string PercentText { get; set; }

    /// <summary>The percent as a bare number (e.g. "36"), shown large in the gauge center
    /// with a separate "%" beneath it.</summary>
    [ObservableProperty]
    public partial string PercentNumber { get; set; }

    [ObservableProperty]
    public partial string ResetText { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPaceWarning))]
    public partial string PaceText { get; set; }

    public bool HasPaceWarning => !string.IsNullOrEmpty(PaceText);

    [ObservableProperty]
    public partial UsageLevel PaceLevel { get; set; }

    [ObservableProperty]
    public partial UsageLevel Level { get; set; }

    public void Update(UsageWindow window)
    {
        Label = window.Label;
        FamilyLabel = window.GroupLabel ?? string.Empty;
        Percent = Math.Clamp(window.UsedRatio, 0.0, 1.0) * 100.0;
        PercentText = $"{window.UsedRatio * 100:0}%";
        PercentNumber = $"{window.UsedRatio * 100:0}";
        Level = UsageLevelClassifier.Classify(window.UsedRatio);
        ResetText = ResetTimeFormatter.ForRow(window.ResetTime);
        (PaceText, PaceLevel) = UsagePaceClassifier.ForRow(window);
    }
}
