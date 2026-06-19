using Microsoft.UI.Xaml.Markup;

namespace Gauge.Localization;

/// <summary>
/// XAML markup extension that resolves a localized string by key, e.g.
/// <c>Text="{loc:Localize Key=Settings}"</c>. The language is fixed before any window's
/// XAML is loaded, so resolving once at parse time (here) is sufficient — there is no
/// in-app language switch to react to.
/// </summary>
public sealed class LocalizeExtension : MarkupExtension
{
    /// <summary>Key into <see cref="Strings.Table"/>.</summary>
    public string Key { get; set; } = string.Empty;

    protected override object ProvideValue() => Loc.Get(Key);
}
