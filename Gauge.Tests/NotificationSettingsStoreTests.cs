using Gauge.Localization;
using Gauge.Models;
using Gauge.Services;

namespace Gauge.Tests;

/// <summary>
/// Persistence validation for <see cref="NotificationSettingsStore"/>: an absent/malformed
/// file defaults every flag to enabled, and saving must not clobber other keys sharing
/// <c>settings.json</c> (the tool registration and UI language).
/// </summary>
public sealed class NotificationSettingsStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "GaugeTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void MissingFileDefaultsToAllEnabled()
        => Assert.Equal(NotificationPreferences.Default, new NotificationSettingsStore(() => _dir).Load());

    [Fact]
    public void MalformedJsonDefaultsToAllEnabled()
    {
        WriteSettings("{ not valid json");
        Assert.Equal(NotificationPreferences.Default, new NotificationSettingsStore(() => _dir).Load());
    }

    [Fact]
    public void FileFromBeforeKindTogglesReadsKindsAsEnabled()
    {
        // A settings file written by an older build knows only the master flag; the new
        // per-kind keys must read as enabled, keeping the prior behavior.
        WriteSettings("""{ "NotificationsEnabled": false }""");
        var loaded = new NotificationSettingsStore(() => _dir).Load();
        Assert.False(loaded.Enabled);
        Assert.True(loaded.Thresholds);
        Assert.True(loaded.Resets);
    }

    [Theory]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(true, false, false)]
    public void SaveThenLoadRoundTrips(bool enabled, bool thresholds, bool resets)
    {
        var store = new NotificationSettingsStore(() => _dir);
        store.Save(new NotificationPreferences(enabled, thresholds, resets));
        Assert.Equal(new NotificationPreferences(enabled, thresholds, resets), store.Load());
    }

    [Fact]
    public void SavingLeavesOtherKeysIntact()
    {
        WriteSettings("""{ "EnabledTools": ["Cursor"], "Language": "ja" }""");

        new NotificationSettingsStore(() => _dir).Save(NotificationPreferences.Default with { Enabled = false });

        // The notifications flags persisted, and the sibling keys survived the round-trip.
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
