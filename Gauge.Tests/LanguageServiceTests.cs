using System.Globalization;
using Gauge.Localization;
using Gauge.Services;

namespace Gauge.Tests;

/// <summary>
/// Language resolution rules: a valid persisted choice always wins; otherwise the OS UI
/// culture is mapped to a supported language, falling back to English for anything that
/// is neither Korean nor Japanese.
/// </summary>
public sealed class LanguageServiceTests
{
    [Theory]
    [InlineData("ko", AppLanguage.Korean)]
    [InlineData("en", AppLanguage.English)]
    [InlineData("ja", AppLanguage.Japanese)]
    public void PersistedCodeWinsOverOsCulture(string persisted, AppLanguage expected)
    {
        // OS says Korean, but the stored choice must take precedence.
        var resolved = LanguageService.Resolve(persisted, CultureInfo.GetCultureInfo("ko-KR"));
        Assert.Equal(expected, resolved);
    }

    [Theory]
    [InlineData("ko-KR", AppLanguage.Korean)]
    [InlineData("ja-JP", AppLanguage.Japanese)]
    [InlineData("en-US", AppLanguage.English)]
    [InlineData("fr-FR", AppLanguage.English)] // unsupported → English fallback
    [InlineData("zh-CN", AppLanguage.English)]
    public void DetectsFromOsCultureWhenNothingPersisted(string culture, AppLanguage expected)
    {
        Assert.Equal(expected, LanguageService.Resolve(null, CultureInfo.GetCultureInfo(culture)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("xx")]
    [InlineData("english")]
    public void InvalidPersistedCodeFallsBackToOsDetection(string persisted)
    {
        // Garbage stored value is ignored; Japanese OS culture is detected instead.
        Assert.Equal(AppLanguage.Japanese, LanguageService.Resolve(persisted, CultureInfo.GetCultureInfo("ja-JP")));
    }

    [Theory]
    [InlineData(AppLanguage.Korean, "ko")]
    [InlineData(AppLanguage.English, "en")]
    [InlineData(AppLanguage.Japanese, "ja")]
    public void CodeRoundTrips(AppLanguage language, string code)
    {
        Assert.Equal(code, language.ToCode());
        Assert.True(AppLanguageExtensions.TryParseCode(code, out var parsed));
        Assert.Equal(language, parsed);
    }
}
