using System.Collections.ObjectModel;
using System.Globalization;
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

    public ToolCardViewModel(CachedUsage cached, IUsageHistorySource? history = null, string? accountLabel = null)
    {
        _history = history;
        ToolName = cached.ToolName;
        var descriptor = StatusTargetMatcher.Descriptor(ToolName);
        DisplayName = descriptor?.DisplayName ?? ToolName;
        AccountLabel = accountLabel;
        LogoGeometry = descriptor is null ? null : ToolLogoGeometry.For(descriptor.Kind);
        StatusText = string.Empty;
        Plan = string.Empty;
        ResetCreditRemaining = string.Empty;
        ResetCreditsDescription = string.Empty;
        Update(cached);
    }

    /// <summary>Stable across updates; used to reconcile cards.</summary>
    public string ToolName { get; }
    public string DisplayName { get; }
    public string? AccountLabel { get; }
    public bool HasAccountLabel => AccountLabel is not null;

    /// <summary>XAML path data of the tool's logo; null for a tool the catalog does not know.</summary>
    public string? LogoGeometry { get; }
    public bool HasLogo => LogoGeometry is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowHeaderChipLabels), nameof(WarningResetCreditsText))]
    public partial bool HasServiceStatus { get; set; }
    [ObservableProperty]
    public partial string ServiceStatusText { get; set; } = "";
    [ObservableProperty]
    public partial string ServiceStatusDescription { get; set; } = "";
    public Uri? StatusPageUrl => StatusTargetMatcher.Descriptor(ToolName)?.StatusPageUrl;

    public void ApplyServiceStatus(ProviderStatus? status)
    {
        HasServiceStatus = status is not null && ProviderStatusText.IsVisible(status);
        ServiceStatusText = status is null ? "" : ProviderStatusText.Label(status);
        ServiceStatusDescription = status is null ? "" : ProviderStatusText.Description(status);
    }

    /// <summary>One row per window the tool exposes, in provider order.</summary>
    public ObservableCollection<UsageWindowRowViewModel> Windows { get; } = new();

    /// <summary>Gauges per row in the gauge layout; matches the two-up width of the popover.</summary>
    private const int GaugeColumns = 2;

    /// <summary>
    /// The same windows packed into rows of <see cref="GaugeColumns"/> for the gauge layout (a divider
    /// sits where a row boundary meets a family change). Each row's items are shared instances
    /// from <see cref="Windows"/>.
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

    /// <summary>
    /// Whether each row's percent shows the used or the remaining share. App-wide, like
    /// <see cref="ViewMode"/>; set by the owning <see cref="UsageViewModel"/> and pushed to
    /// every row here, so rows created later inherit it and existing rows re-derive their
    /// percent in place.
    /// </summary>
    [ObservableProperty]
    public partial UsageDisplayBasis DisplayBasis { get; set; }

    partial void OnDisplayBasisChanged(UsageDisplayBasis value)
    {
        foreach (var row in Windows)
        {
            row.DisplayBasis = value;
        }
    }

    /// <summary>
    /// Whether bar rows show the burndown sparkline. App-wide like <see cref="DisplayBasis"/>;
    /// pushed to every row so rows created later inherit it.
    /// </summary>
    [ObservableProperty]
    public partial bool ShowSparkline { get; set; } = true;

    [ObservableProperty]
    public partial bool ShowDailyUsage { get; set; }

    partial void OnShowDailyUsageChanged(bool value)
    {
        foreach (var row in Windows) row.ShowDailyUsage = value;
    }

    private ApiCostEstimate? _apiCost;

    partial void OnShowSparklineChanged(bool value)
    {
        foreach (var row in Windows)
        {
            row.ShowSparkline = value;
        }
    }

    /// <summary>
    /// How multi-day windows' quota is expected to spread across the week (the pace
    /// caption's reference curve). App-wide like <see cref="DisplayBasis"/>; pushed to every
    /// row so rows created later inherit it and existing rows re-derive their caption.
    /// </summary>
    [ObservableProperty]
    public partial WeeklyPaceModel PaceModel { get; set; } = WeeklyPaceModel.Uniform;

    partial void OnPaceModelChanged(WeeklyPaceModel value)
    {
        foreach (var row in Windows)
        {
            row.PaceModel = value;
        }
    }

    /// <summary>Plan/subscription label shown beside the tool name (e.g. "Max 5x").</summary>
    [ObservableProperty]
    public partial string Plan { get; set; }

    /// <summary>True when a plan label is available (controls its visibility).</summary>
    [ObservableProperty]
    public partial bool HasPlan { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasResetCreditWarning), nameof(ShowHeaderChipLabels), nameof(WarningResetCreditsText))]
    public partial string ResetCreditRemaining { get; set; }

    [ObservableProperty]
    public partial string ResetCreditsDescription { get; set; }

    public bool HasResetCreditWarning => ResetCreditRemaining.Length > 0;

    public bool ShowHeaderChipLabels => !(HasApiCost && HasServiceStatus && HasResetCreditWarning);

    public string WarningResetCreditsText => ShowHeaderChipLabels
        ? Loc.Format("ResetCredit_Label", ResetCreditRemaining) : ResetCreditRemaining;

    /// <summary>
    /// This month's API-equivalent cost beside the plan label ("≈ $12.34"), from the local
    /// session logs at public list rates; "+" marks a floor when some model had no known
    /// rate. Empty when the option is off, nothing was scanned yet, or the month is empty.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasApiCost))]
    [NotifyPropertyChangedFor(nameof(ShowHeaderChipLabels), nameof(WarningResetCreditsText))]
    public partial string ApiCostText { get; set; } = "";

    /// <summary>Spelled-out form for the tooltip.</summary>
    [ObservableProperty]
    public partial string ApiCostDescription { get; set; } = "";

    /// <summary>
    /// The automation name: the amount itself, then the explanation. The amount has to lead —
    /// an automation name replaces the visible text for a screen reader, so the description
    /// alone would never say the number.
    /// </summary>
    [ObservableProperty]
    public partial string ApiCostAccessibleName { get; set; } = "";

    public bool HasApiCost => !string.IsNullOrEmpty(ApiCostText);

    private const decimal WholeDollarThreshold = 100m;

    /// <summary>Shows or clears the cost estimate; null clears it (option off).</summary>
    public void ApplyApiCost(ApiCostEstimate? estimate)
    {
        _apiCost = estimate;
        foreach (var row in Windows) ApplyDailyCost(row);
        if (estimate is null || estimate.IsEmpty)
        {
            ApiCostText = "";
            ApiCostDescription = "";
            ApiCostAccessibleName = "";
            return;
        }
        // Dollars are machine-facing here (a fixed "12.34" / "1,235" shape), so the invariant
        // culture formats them; the token counts take the UI language's digit grouping. From
        // $100 the cents are dropped: an estimate gains nothing from them, and the header row
        // does not trim, so a longer amount would be clipped. The threshold is checked after
        // rounding to cents so 99.996 reads "100", not "100.00".
        var cents = Math.Round(estimate.CostUsd, 2, MidpointRounding.AwayFromZero);
        var dollars = cents >= WholeDollarThreshold
            ? cents.ToString("#,##0", CultureInfo.InvariantCulture)
            : cents.ToString("0.00", CultureInfo.InvariantCulture);
        ApiCostText = Loc.Format(estimate.HasUnpriced ? "ApiCost_ValueFloor" : "ApiCost_Value", dollars);
        var tokens = estimate.PricedTokens;
        var description = Loc.Format("Tooltip_ApiCost",
            Count(tokens.Input), Count(tokens.CacheRead), Count(tokens.CacheWrite + tokens.CacheWrite1h), Count(tokens.Output));
        if (estimate.HasUnpriced)
        {
            description += Loc.Format("ApiCost_Unpriced", string.Join(", ", estimate.UnpricedModels), Count(estimate.UnpricedTokens));
        }
        ApiCostDescription = description;
        ApiCostAccessibleName = ApiCostText + ". " + description;

        static string Count(long value) => string.Format(Loc.Culture, "{0:N0}", value);
    }

    private void ApplyDailyCost(UsageWindowRowViewModel row)
    {
        // Local logs cover the default tool, not named accounts or model-scoped quotas.
        row.SetDailyCosts(AccountLabel is null && !ToolName.Contains(ToolAccount.KeySeparator)
            && !row.HasFamilyLabel ? _apiCost?.Days ?? [] : []);
    }

    [ObservableProperty]
    public partial bool HasAnyData { get; set; }

    [ObservableProperty]
    public partial bool HasUsageWindows { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSupplementalUsage))]
    public partial string SupplementalUsage { get; set; } = "";

    public bool HasSupplementalUsage => SupplementalUsage.Length > 0;

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

        var now = DateTimeOffset.UtcNow;
        var deadline = ResetCreditDeadline.Upcoming(cached.Snapshot?.ResetCreditDeadlines, now);
        var supplemental = cached.Snapshot?.ResetCreditDeadlines?
            .Where(d => d.ExpiresAt > now && d.Count is > 0)
            .GroupBy(d => d.Scope)
            .Select(group =>
            {
                var label = Loc.Format("Usage_ResetBalance", group.Sum(d => (long)d.Count!.Value));
                return group.Key is { } scope ? $"{scope} · {label}" : label;
            }).ToList() ?? [];
        if (cached.Snapshot?.SourceLabel is { } source)
            supplemental.Add(Loc.Format("Usage_Source", source));
        if (cached.Snapshot?.AiCreditsUsed is { } credits)
            supplemental.Add(Loc.Format("Usage_AiCreditsUsed", credits));
        SupplementalUsage = string.Join("\n", supplemental);
        ResetCreditRemaining = deadline is null ? string.Empty : ResetCreditText.Remaining(deadline, now);
        ResetCreditsDescription = deadline is null ? string.Empty : ResetCreditText.Description(deadline);

        var windows = OrderForDisplay(cached.Snapshot?.Windows ?? Array.Empty<UsageWindow>());
        HasUsageWindows = windows.Count > 0;
        var historyKey = cached.Snapshot?.HistoryKey ?? ToolName;

        if (windows.Count == 0)
        {
            HasAnyData = HasSupplementalUsage;
            StatusText = HasAnyData ? "" : Loc.Get("NoData");
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
            // The weekday profile behind the automatic pace model, and the ETA from the
            // recorded burn rate. Both history reads are memory-backed after the first, so
            // this stays cheap on the UI thread. The profile is set before Update so an
            // existing row derives its caption once, against the current profile.
            var profile = _history?.GetWeekdayProfile(historyKey, window.Key);
            var existing = Windows.FirstOrDefault(r => r.Key == window.Key);
            if (existing is null)
            {
                existing = new UsageWindowRowViewModel(window, DisplayBasis)
                {
                    ShowSparkline = ShowSparkline, PaceModel = PaceModel, WeekdayProfile = profile,
                    ShowDailyUsage = ShowDailyUsage,
                };
                Windows.Insert(Math.Min(index, Windows.Count), existing);
            }
            else
            {
                existing.WeekdayProfile = profile;
                existing.Update(window);
            }
            var samples = _history?.GetRecent(historyKey, window.Key, UsageBurndown.Lookback) ?? [];
            existing.EtaText = UsageEtaClassifier.ForRow(window, samples);
            // The row re-resolves any active hover against the new points itself.
            existing.Burndown = UsageBurndown.Build(window, samples, DateTimeOffset.UtcNow);
            existing.DailySegments = window.Type == UsageWindowType.Weekly
                ? UsageDailyBreakdown.Build(window,
                    _history?.GetRecent(historyKey, window.Key, UsageDailyBreakdown.Lookback) ?? [],
                    cached.Snapshot!.CapturedAt) : [];
            ApplyDailyCost(existing);
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

    // Packs the (already display-ordered) windows into rows of GaugeColumns for the gauge layout.
    // Rows are filled across families, so a lone scoped window (Fable) sits beside the account-wide
    // one instead of leaving the row above it half empty; a divider is drawn only where a row
    // boundary happens to coincide with a family change. The row containers are reconciled in
    // place (keyed by their first window), and the items are the same instances as Windows, so
    // their values update without a rebuild.
    private void RebuildGaugeGroups(IReadOnlyList<UsageWindow> ordered)
    {
        var grouped = ordered.Any(w => !string.IsNullOrEmpty(w.GroupLabel));

        var placed = new List<(UsageWindowRowViewModel Row, string Family)>();
        foreach (var window in ordered)
        {
            if (Windows.FirstOrDefault(r => r.Key == window.Key) is { } row)
            {
                placed.Add((row, grouped ? window.GroupLabel ?? string.Empty : string.Empty));
            }
        }

        var bands = placed.Chunk(GaugeColumns).ToList();
        var keysInOrder = bands.Select(b => b[0].Row.Key).ToList();

        for (var i = GaugeGroups.Count - 1; i >= 0; i--)
        {
            if (!keysInOrder.Contains(GaugeGroups[i].Key))
            {
                GaugeGroups.RemoveAt(i);
            }
        }

        for (var index = 0; index < bands.Count; index++)
        {
            var key = keysInOrder[index];
            var band = GaugeGroups.FirstOrDefault(g => g.Key == key);
            if (band is null)
            {
                band = new GaugeGroupViewModel(key);
                GaugeGroups.Insert(Math.Min(index, GaugeGroups.Count), band);
            }
            else if (GaugeGroups.IndexOf(band) != index)
            {
                GaugeGroups.Move(GaugeGroups.IndexOf(band), Math.Min(index, GaugeGroups.Count - 1));
            }
            band.ShowDivider = index > 0 && bands[index - 1][^1].Family != bands[index][0].Family;
            ReconcileRows(band.Rows, bands[index].Select(p => p.Row).ToList());

            var anyFamily = bands[index].Any(p => p.Row.HasFamilyLabel);
            foreach (var (row, _) in bands[index])
            {
                row.ReservesFamilyLine = anyFamily && !row.HasFamilyLabel;
            }
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
