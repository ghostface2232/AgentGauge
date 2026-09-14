using Gauge.Localization;
using Gauge.Models;
using Gauge.Services;

namespace Gauge.Tests;

/// <summary>
/// The read-modify-write contract of the shared <c>settings.json</c>. The case that matters
/// is the one where the read fails: every store writes one key into a document the rest of
/// the app shares, so a write performed against an empty default would replace whatever the
/// file holds with just that key.
/// </summary>
public sealed class AppSettingsFileTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "GaugeTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void SavingWithNoExistingFileCreatesIt()
    {
        Assert.True(AppSettingsFile.TrySave(_dir, dto => dto.Language = "ja"));
        Assert.Equal("ja", AppSettingsFile.Load(_dir).Language);
    }

    [Fact]
    public void SavingPreservesKeysTheWriterDidNotTouch()
    {
        WriteSettings("""{ "EnabledTools": ["Cursor"], "Language": "ja", "UnknownFutureKey": 7 }""");

        Assert.True(AppSettingsFile.TrySave(_dir, dto => dto.ShowSparkline = false));

        var reloaded = AppSettingsFile.Load(_dir);
        Assert.Equal(new[] { "Cursor" }, reloaded.EnabledTools!);
        Assert.Equal("ja", reloaded.Language);
        Assert.False(reloaded.ShowSparkline);
        Assert.Contains("UnknownFutureKey", File.ReadAllText(SettingsPath));
    }

    [Fact]
    public void SavingOverAnUnreadableFileIsRefusedAndLeavesItByteIdentical()
    {
        // The file is malformed, so the load that a read-modify-write starts from yields an
        // empty default. Writing that back would erase EnabledTools, HiddenTools, Language
        // and every other store's key — so nothing may be written at all.
        const string corrupt = """{ "EnabledTools": ["Cursor"], "Language": "ja", """;
        WriteSettings(corrupt);

        Assert.False(AppSettingsFile.TrySave(_dir, dto => dto.ShowSparkline = false));

        Assert.Equal(corrupt, File.ReadAllText(SettingsPath));
    }

    [Fact]
    public void RefusingAWriteLeavesNoTemporaryFileBehind()
    {
        WriteSettings("{ not valid json");

        AppSettingsFile.Save(_dir, dto => dto.ViewMode = "gauge");

        Assert.False(File.Exists(SettingsPath + ".tmp"));
    }

    [Theory]
    [InlineData("{ not valid json")]
    [InlineData("")]
    public void EveryStoreRefusesToWriteOverAnUnreadableFile(string corrupt)
    {
        WriteSettings(corrupt);

        Assert.False(new ViewModeSettingsStore(() => _dir).TrySave(UsageViewMode.Gauge));
        Assert.False(new DisplayBasisSettingsStore(() => _dir).TrySave(UsageDisplayBasis.Remaining));
        Assert.False(new SparklineSettingsStore(() => _dir).TrySave(false));
        Assert.False(new NotificationSettingsStore(() => _dir).TrySave(new NotificationPreferences(false, false)));
        Assert.False(LanguageService.SaveOverride(AppLanguage.English, _dir));

        Assert.Equal(corrupt, File.ReadAllText(SettingsPath));
    }

    private string SettingsPath => Path.Combine(_dir, "settings.json");

    private void WriteSettings(string json)
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(SettingsPath, json);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }
}
