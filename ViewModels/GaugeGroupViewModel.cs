using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Gauge.ViewModels;

/// <summary>
/// One row of gauges in gauge mode — a model/family scope for grouped tools, or all of an
/// ungrouped tool's windows. Groups let the gauge layout draw a full-width divider
/// between families (<see cref="ShowDivider"/>), mirroring the bar layout's separator. The
/// <see cref="Rows"/> are shared with the card's flat <c>Windows</c> collection, so their
/// values update in place without rebuilding the group.
/// </summary>
public sealed partial class GaugeGroupViewModel : ObservableObject
{
    public GaugeGroupViewModel(string key) => Key = key;

    /// <summary>Family label for grouped tools, or empty for the single ungrouped group.</summary>
    public string Key { get; }

    public ObservableCollection<UsageWindowRowViewModel> Rows { get; } = new();

    /// <summary>Whether a separator is drawn above this group (set on every group but the first).</summary>
    [ObservableProperty]
    public partial bool ShowDivider { get; set; }
}
