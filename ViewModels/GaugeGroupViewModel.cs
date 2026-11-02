using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Gauge.ViewModels;

/// <summary>
/// One row of gauges in gauge mode. Rows are packed across model/family scopes so none is left
/// half empty; <see cref="ShowDivider"/> draws a full-width separator above a row only when the
/// family changes exactly at the row boundary, mirroring the bar layout's separator. The
/// <see cref="Rows"/> are shared with the card's flat <c>Windows</c> collection, so their
/// values update in place without rebuilding the row.
/// </summary>
public sealed partial class GaugeGroupViewModel : ObservableObject
{
    public GaugeGroupViewModel(string key) => Key = key;

    /// <summary>Key of the row's first window; stable identity for reconciling rows across refreshes.</summary>
    public string Key { get; }

    public ObservableCollection<UsageWindowRowViewModel> Rows { get; } = new();

    /// <summary>Whether a separator is drawn above this row.</summary>
    [ObservableProperty]
    public partial bool ShowDivider { get; set; }
}
