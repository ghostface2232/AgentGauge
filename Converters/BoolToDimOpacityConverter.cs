using Microsoft.UI.Xaml.Data;

namespace Gauge.Converters;

/// <summary>
/// Bool → opacity for dependent settings rows: 1.0 when the controlling switch is on,
/// dimmed when off. Used with IsHitTestVisible so a disabled-looking row is also inert.
/// </summary>
public sealed class BoolToDimOpacityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
        => value is bool b && b ? 1.0 : 0.4;

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}
