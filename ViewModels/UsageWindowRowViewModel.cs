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
    // The provider's used fraction (unclamped), kept so the shown percent can be re-derived
    // when the display basis flips without waiting for the next refresh.
    private double _usedRatio;

    public UsageWindowRowViewModel(UsageWindow window, UsageDisplayBasis displayBasis = UsageDisplayBasis.Used)
    {
        Key = window.Key;
        Label = string.Empty;
        FamilyLabel = string.Empty;
        GroupHeader = string.Empty;
        PercentText = string.Empty;
        PercentNumber = string.Empty;
        ResetText = string.Empty;
        CountsText = string.Empty;
        EtaText = string.Empty;
        PaceText = string.Empty;
        // A non-default basis runs the change hook here (against a zero used ratio); Update
        // below then derives the real percent, so the transient value is never observed.
        DisplayBasis = displayBasis;
        Update(window);
    }

    /// <summary>Provider-stable key used to reconcile rows across refreshes.</summary>
    public string Key { get; }

    /// <summary>Window label (e.g. "5시간", "주간").</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AccessibilityName))]
    public partial string Label { get; set; }

    /// <summary>
    /// Model-family name for this window (e.g. "Gemini", "Claude/GPT"), empty for ungrouped
    /// tools. Unlike <see cref="GroupHeader"/> — which the card sets only on the first row of
    /// each group for the bar layout — this is the window's own family and is shown on every
    /// gauge in gauge mode, where each gauge stands alone in a grid cell.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFamilyLabel))]
    [NotifyPropertyChangedFor(nameof(AccessibilityName))]
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

    /// <summary>
    /// Whether <see cref="Percent"/>, <see cref="PercentText"/> and <see cref="PercentNumber"/>
    /// express the used or the remaining share of the window. App-wide; the owning card pushes
    /// the setting here. <see cref="Level"/> is unaffected — it always tracks the used share.
    /// </summary>
    [ObservableProperty]
    public partial UsageDisplayBasis DisplayBasis { get; set; }

    /// <summary>0–100 for the progress bar, in the current <see cref="DisplayBasis"/>.</summary>
    [ObservableProperty]
    public partial double Percent { get; set; }

    [ObservableProperty]
    public partial string PercentText { get; set; }

    /// <summary>The percent as a bare number (e.g. "36"), shown large in the gauge center
    /// with a separate "%" beneath it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AccessibilityName))]
    public partial string PercentNumber { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AccessibilityName))]
    public partial string ResetText { get; set; }

    /// <summary>
    /// Absolute counts behind the percent (e.g. "128 / 300" premium requests), shown as a
    /// dimmed caption in bar mode when the provider reported them. Empty otherwise — most
    /// APIs expose percent only, and the row must never fabricate a denominator.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCounts))]
    [NotifyPropertyChangedFor(nameof(AccessibilityName))]
    public partial string CountsText { get; set; }

    public bool HasCounts => !string.IsNullOrEmpty(CountsText);

    /// <summary>
    /// Projected exhaustion caption from the measured recent burn rate (e.g. "2시간 후 소진
    /// 예상"), set by the owning card from the usage history. Empty when the projection has
    /// no trustworthy basis or exhaustion lands after the reset.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasEta))]
    [NotifyPropertyChangedFor(nameof(AccessibilityName))]
    public partial string EtaText { get; set; }

    public bool HasEta => !string.IsNullOrEmpty(EtaText);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPace))]
    [NotifyPropertyChangedFor(nameof(AccessibilityName))]
    public partial string PaceText { get; set; }

    public bool HasPace => !string.IsNullOrEmpty(PaceText);

    [ObservableProperty]
    public partial UsageLevel PaceLevel { get; set; }

    [ObservableProperty]
    public partial UsageLevel Level { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBurndown))]
    public partial IReadOnlyList<BurndownPoint> Burndown { get; set; } = [];
    public bool HasBurndown => Burndown.Count >= UsageBurndown.MinimumSamples;
    // Normalized pointer position over the sparkline while hovered; null when the pointer
    // left. Kept across data updates so a background refresh re-selects the nearest sample
    // under a stationary pointer instead of dropping the caption back to the reset text.
    private double? _hoverX;
    private string? _hoverCaption;
    public string CaptionText => _hoverCaption ?? ResetText;
    partial void OnResetTextChanged(string value) => OnPropertyChanged(nameof(CaptionText));
    partial void OnBurndownChanged(IReadOnlyList<BurndownPoint> value) => HoverBurndown(HasBurndown ? _hoverX : null);

    public void HoverBurndown(double? x)
    {
        _hoverX = x;
        _hoverCaption = x is { } position && HasBurndown && UsageBurndown.Nearest(Burndown, position) is { } point
            ? Loc.Format("Burndown_Hover", point.Sample.CapturedAt.ToLocalTime().ToString("HH:mm", Loc.Culture), point.Remaining * 100)
            : null;
        OnPropertyChanged(nameof(CaptionText));
    }

    // ResetText is empty when the provider omits a reset time, so it joins as an optional
    // part instead of a format slot that would leave a dangling separator. The pace dash is
    // a visual placeholder with no spoken meaning.
    public string AccessibilityName => string.Join(". ", new[]
    {
        Loc.Format(DisplayBasis == UsageDisplayBasis.Remaining ? "Usage_AccessibleRemaining" : "Usage_Accessible",
            string.Join(" ", new[] { FamilyLabel, Label }.Where(s => !string.IsNullOrEmpty(s))), PercentNumber),
        ResetText, CountsText, PaceText == UsagePaceClassifier.UnavailableText ? null : PaceText, EtaText,
    }.Where(s => !string.IsNullOrEmpty(s)));

    // The spoken name changes wording ("used" vs "remaining") even when the number happens to
    // be the same in both bases (50%), so it is re-raised explicitly rather than relying on
    // PercentNumber's change notification.
    partial void OnDisplayBasisChanged(UsageDisplayBasis value)
    {
        ApplyPercent();
        OnPropertyChanged(nameof(AccessibilityName));
    }

    // Derives the shown percent from the provider's used fraction in the current basis. The
    // used basis keeps the raw (unclamped) number in its text so an over-limit window can read
    // "104%". The remaining number is 100 minus the *rounded* used number — not the rounded
    // complement — so the two bases always sum to 100 (99.5% used reads 100% / 0%, never
    // 100% / 1%), and it is clamped to 0–100 since there is no negative or excess remaining.
    private void ApplyPercent()
    {
        var usedPercent = _usedRatio * 100.0;
        if (DisplayBasis == UsageDisplayBasis.Remaining)
        {
            var usedRounded = Math.Round(usedPercent, MidpointRounding.AwayFromZero);
            var remaining = Math.Clamp(100.0 - usedRounded, 0.0, 100.0);
            Percent = Math.Clamp(100.0 - usedPercent, 0.0, 100.0);
            PercentText = $"{remaining:0}%";
            PercentNumber = $"{remaining:0}";
        }
        else
        {
            Percent = Math.Clamp(usedPercent, 0.0, 100.0);
            PercentText = $"{usedPercent:0}%";
            PercentNumber = $"{usedPercent:0}";
        }
    }

    public void Update(UsageWindow window)
    {
        Label = window.Label;
        FamilyLabel = window.GroupLabel ?? string.Empty;
        _usedRatio = window.UsedRatio;
        ApplyPercent();
        Level = UsageLevelClassifier.Classify(window.UsedRatio);
        ResetText = ResetTimeFormatter.ForRow(window.ResetTime);
        // Loc.Culture (not the ambient culture) for deterministic digit grouping.
        CountsText = window is { UsedTokens: { } used, LimitTokens: { } limit }
            ? string.Format(Loc.Culture, "{0:N0} / {1:N0}", used, limit)
            : string.Empty;
        (PaceText, PaceLevel) = UsagePaceClassifier.ForRow(window);
    }

    /// <summary>
    /// Re-raises the level properties without changing them, so level-to-brush bindings
    /// re-run their converter after a live theme change. The resolved brush depends on
    /// the theme — which the binding system cannot observe — so the owning window nudges
    /// every row through here when <c>ActualTheme</c> flips.
    /// </summary>
    public void RefreshLevelBrushes()
    {
        OnPropertyChanged(nameof(Level));
        OnPropertyChanged(nameof(PaceLevel));
    }
}
