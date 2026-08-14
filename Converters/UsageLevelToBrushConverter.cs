using Gauge.Models;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using Windows.UI.ViewManagement;

namespace Gauge.Converters;

/// <summary>
/// Maps a <see cref="UsageLevel"/> to its named color brush resource (defined per theme
/// in App.xaml), so colors live in resources rather than inline in the UI. With
/// ConverterParameter=text it resolves the <c>Usage*TextBrush</c> variants instead of the
/// fill brushes — small text needs darker/brighter colors than a bar fill to stay
/// readable on the themed acrylic.
/// </summary>
public sealed class UsageLevelToBrushConverter : IValueConverter
{
    /// <summary>
    /// The live theme to resolve against, kept current by the owning window from
    /// <c>ActualThemeChanged</c>. <see cref="ElementTheme.Default"/> falls back to the
    /// application's launch theme. Needed because <c>Application.RequestedTheme</c> is a
    /// launch-time snapshot: without this a mid-session OS theme switch would keep
    /// serving the old theme's colors from code lookups (XAML ThemeResource references
    /// track the switch on their own; converter output cannot).
    /// </summary>
    public ElementTheme Theme { get; set; } = ElementTheme.Default;

    // High-contrast detection: in HC the framework's own lookup prefers the
    // HighContrast dictionary, so the explicit light/dark resolution must step aside.
    // Constructed defensively — it needs Windows.UI.ViewManagement to be available.
    private static readonly AccessibilitySettings? Accessibility = CreateAccessibility();

    private static AccessibilitySettings? CreateAccessibility()
    {
        try
        {
            return new AccessibilitySettings();
        }
        catch
        {
            return null;
        }
    }

    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var name = value is UsageLevel level
            ? level switch
            {
                UsageLevel.Danger => "UsageDanger",
                UsageLevel.Caution => "UsageCaution",
                _ => "UsageOk",
            }
            : "UsageOk";
        var key = (parameter as string) == "text" ? $"{name}TextBrush" : $"{name}Brush";

        return Lookup(key) ?? new SolidColorBrush(Colors.Gray);
    }

    private object? Lookup(string key)
    {
        var resources = Application.Current.Resources;

        // Resolve from the live theme's dictionary first (see Theme). Skipped in high
        // contrast and when no live theme is known — then the framework lookup below
        // picks the right dictionary itself (HighContrast, or the launch theme).
        var highContrast = false;
        try
        {
            highContrast = Accessibility?.HighContrast == true;
        }
        catch
        {
            // Treat a failed read as "not high contrast" — worst case is the same
            // launch-theme resolution the framework fallback would do anyway.
        }
        if (!highContrast && Theme is ElementTheme.Light or ElementTheme.Dark)
        {
            var themeKey = Theme == ElementTheme.Light ? "Light" : "Default";
            if (resources.ThemeDictionaries.TryGetValue(themeKey, out var entry)
                && entry is ResourceDictionary themed
                && themed.TryGetValue(key, out var themedValue))
            {
                return themedValue;
            }
        }

        return resources.TryGetValue(key, out var value) ? value : null;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}
