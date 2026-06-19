using System.Globalization;

namespace Gauge.Localization;

/// <summary>
/// The languages Gauge's UI is translated into. The integer values are the column
/// indices used by <see cref="Strings"/>' translation table, so their order must not
/// change. Korean is index 0 because it is the original language and the uninitialized
/// default (see <see cref="Loc"/>).
/// </summary>
public enum AppLanguage
{
    Korean = 0,
    English = 1,
    Japanese = 2,
}

public static class AppLanguageExtensions
{
    /// <summary>The two-letter code persisted in settings.json ("ko" / "en" / "ja").</summary>
    public static string ToCode(this AppLanguage language) => language switch
    {
        AppLanguage.Korean => "ko",
        AppLanguage.Japanese => "ja",
        _ => "en",
    };

    /// <summary>
    /// A specific culture used for date/number formatting. Specific (not neutral)
    /// cultures give reliable month abbreviations like "Jun" for the English reset text.
    /// </summary>
    public static CultureInfo ToCulture(this AppLanguage language) => language switch
    {
        AppLanguage.Korean => CultureInfo.GetCultureInfo("ko-KR"),
        AppLanguage.Japanese => CultureInfo.GetCultureInfo("ja-JP"),
        _ => CultureInfo.GetCultureInfo("en-US"),
    };

    /// <summary>Parses a persisted "ko" / "en" / "ja" code. Returns false for anything else.</summary>
    public static bool TryParseCode(string? code, out AppLanguage language)
    {
        switch (code)
        {
            case "ko": language = AppLanguage.Korean; return true;
            case "en": language = AppLanguage.English; return true;
            case "ja": language = AppLanguage.Japanese; return true;
            default: language = AppLanguage.Korean; return false;
        }
    }
}
