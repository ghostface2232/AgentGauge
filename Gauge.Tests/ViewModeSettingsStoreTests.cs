using Gauge.Localization;
using Gauge.Models;
using Gauge.Services;

namespace Gauge.Tests;

/// <summary>
/// Persistence validation for <see cref="ViewModeSettingsStore"/>: an absent/malformed or
/// unrecognized value defaults to the bar layout, and saving the mode must not clobber
/// other keys sharing <c>settings.json</c> (tool registration, UI language, notifications).
/// </summary>
public sealed class ViewModeSettingsStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "GaugeTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void MissingFileDefaultsToBar()
        => Assert.Equal(UsageViewMode.Bar, new ViewModeSettingsStore(() => _dir).Load());

    [Fact]
    public void MalformedJsonDefaultsToBar()
    {
        WriteSettings("{ not valid json");
        Assert.Equal(UsageViewMode.Bar, new ViewModeSettingsStore(() => _dir).Load());
    }

    [Fact]
    public void UnknownValueDefaultsToBar()
    {
        WriteSettings("""{ "ViewMode": "circular" }""");
        Assert.Equal(UsageViewMode.Bar, new ViewModeSettingsStore(() => _dir).Load());
    }

    [Theory]
    [InlineData(UsageViewMode.Bar)]
    [InlineData(UsageViewMode.Gauge)]
    public void SaveThenLoadRoundTrips(UsageViewMode mode)
    {
        var store = new ViewModeSettingsStore(() => _dir);
        store.Save(mode);
        Assert.Equal(mode, store.Load());
    }

    [Fact]
    public void SavingLeavesOtherKeysIntact()
    {
        WriteSettings("""{ "EnabledTools": ["Cursor"], "Language": "ja", "NotificationsEnabled": false }""");

        new ViewModeSettingsStore(() => _dir).Save(UsageViewMode.Gauge);

        Assert.Equal(UsageViewMode.Gauge, new ViewModeSettingsStore(() => _dir).Load());
        Assert.False(new NotificationSettingsStore(() => _dir).Load().Enabled);
        Assert.Equal(AppLanguage.Japanese, LanguageService.InitializeFromSettings(_dir));
        Assert.Equal(new[] { ToolKind.Cursor }, new ToolRegistryStore(() => _dir).Load());
    }

    private void WriteSettings(string json)
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "settings.json"), json);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }
}
