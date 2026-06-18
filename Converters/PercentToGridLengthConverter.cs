using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace Gauge.Converters;

/// <summary>
/// Turns a percent (0–100) into a star <see cref="GridLength"/> for a two-column
/// progress bar: the default returns the filled weight, ConverterParameter="rest"
/// returns the remaining weight. Used by the custom progress bar so its thickness
/// and shape are fully controllable (the built-in ProgressBar track is fixed-thin).
/// </summary>
public sealed class PercentToGridLengthConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var percent = value is double d ? Math.Clamp(d, 0.0, 100.0) : 0.0;
        var rest = parameter is string s && string.Equals(s, "rest", StringComparison.OrdinalIgnoreCase);
        return new GridLength(rest ? 100.0 - percent : percent, GridUnitType.Star);
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}
