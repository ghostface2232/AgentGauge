using System.Globalization;
using Gauge.Localization;

namespace Gauge.Services;

/// <summary>
/// Decides the app's UI language and persists the choice. There is no in-app language
/// switch: the first launch detects the language from the OS display language, stores it
/// in settings.json, and every later launch reuses the stored value. A portable/updated
/// launch with no stored value simply re-detects (deterministically the same result).
/// </summary>
public static class LanguageService
{
    /// <summary>
    /// Pure resolution: a valid persisted code wins; otherwise map the OS UI culture to a
    /// supported language, falling back to English for anything that isn't Korean or Japanese.
    /// </summary>
    public static AppLanguage Resolve(string? persisted, CultureInfo osUiCulture)
    {
        if (AppLanguageExtensions.TryParseCode(persisted, out var stored))
        {
            return stored;
        }

        // TwoLetterISOLanguageName is "ko"/"ja"/"en"/… regardless of region.
        return osUiCulture.TwoLetterISOLanguageName switch
        {
            "ko" => AppLanguage.Korean,
            "ja" => AppLanguage.Japanese,
            _ => AppLanguage.English,
        };
    }

    /// <summary>
    /// Resolves the language from settings.json (detecting + persisting on first run) and
    /// returns it. Does not touch <see cref="Loc"/> — the caller applies it via
    /// <see cref="Loc.Initialize"/> so the global side effect stays out of this logic.
    /// </summary>
    public static AppLanguage InitializeFromSettings(string? directory = null)
    {
        var dir = directory ?? AppSettingsFile.DefaultDirectory;
        var stored = AppSettingsFile.Load(dir).Language;
        var language = Resolve(stored, CultureInfo.CurrentUICulture);

        if (!AppLanguageExtensions.TryParseCode(stored, out _))
        {
            // First run (or a cleared/portable settings file): remember what we detected.
            AppSettingsFile.Save(dir, dto => dto.Language = language.ToCode());
        }

        return language;
    }
}
