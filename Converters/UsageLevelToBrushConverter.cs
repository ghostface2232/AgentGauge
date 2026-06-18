using Gauge.Models;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;

namespace Gauge.Converters;

/// <summary>
/// Maps a <see cref="UsageLevel"/> to its named color brush resource (defined in
/// App.xaml), so colors live in resources rather than inline in the UI.
/// </summary>
public sealed class UsageLevelToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var key = value is UsageLevel level
            ? level switch
            {
                UsageLevel.Danger => "UsageDangerBrush",
                UsageLevel.Caution => "UsageCautionBrush",
                _ => "UsageOkBrush",
            }
            : "UsageOkBrush";

        return Application.Current.Resources.TryGetValue(key, out var brush)
            ? brush
            : new SolidColorBrush(Colors.Gray);
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}
