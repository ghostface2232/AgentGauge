using System.Globalization;

namespace Gauge.Localization;

/// <summary>
/// The app-wide localization facade. <see cref="Initialize"/> is called once at startup
/// with the resolved <see cref="AppLanguage"/>; everything else is a lookup against
/// <see cref="Strings.Table"/>.
///
/// The default (before <see cref="Initialize"/> runs) is Korean — the original language.
/// This keeps unit tests, which never call Initialize and assert Korean output, passing
/// unchanged. Formatting always uses <see cref="Culture"/> (derived from the selected
/// language) rather than the ambient thread culture, so output is deterministic.
/// </summary>
public static class Loc
{
    private static AppLanguage _current = AppLanguage.Korean;

    /// <summary>The active UI language.</summary>
    public static AppLanguage Current => _current;

    /// <summary>The culture used for date/number formatting of localized strings.</summary>
    public static CultureInfo Culture { get; private set; } = AppLanguage.Korean.ToCulture();

    /// <summary>
    /// Sets the active language and aligns the thread/default cultures with it so that
    /// any culture-aware formatting elsewhere matches the chosen language.
    /// </summary>
    public static void Initialize(AppLanguage language)
    {
        _current = language;
        Culture = language.ToCulture();
        CultureInfo.CurrentCulture = Culture;
        CultureInfo.CurrentUICulture = Culture;
        CultureInfo.DefaultThreadCurrentCulture = Culture;
        CultureInfo.DefaultThreadCurrentUICulture = Culture;
    }

    /// <summary>
    /// Returns the localized string for <paramref name="key"/>. Falls back to English,
    /// then to the key itself, so a missing translation is visible rather than blank.
    /// </summary>
    public static string Get(string key)
    {
        if (Strings.Table.TryGetValue(key, out var byLanguage))
        {
            return byLanguage[(int)_current]
                ?? byLanguage[(int)AppLanguage.English]
                ?? key;
        }
        return key;
    }

    /// <summary>Localized <see cref="Get"/> result formatted with <see cref="Culture"/>.</summary>
    public static string Format(string key, params object?[] args)
        => string.Format(Culture, Get(key), args);
}
