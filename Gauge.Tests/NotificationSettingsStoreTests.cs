using Gauge.Localization;
using Gauge.Models;
using Gauge.Services;

namespace Gauge.Tests;

/// <summary>
/// Persistence validation for <see cref="NotificationSettingsStore"/>: an absent/malformed
/// file defaults every flag to enabled, a legacy master pause carries over as silence, and
/// saving must not clobber other keys sharing <c>settings.json</c> (the tool registration
/// and UI language).
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
    public void FileWithLegacyMasterPauseReadsEveryKindAsOff()
    {
        // An older build's master pause is the only record that the user wanted silence, so
        // it must carry over as both kinds off — otherwise upgrading starts alerting again.
        WriteSettings("""{ "NotificationsEnabled": false, "NotifyThresholds": true, "NotifyResets": true }""");
        var loaded = new NotificationSettingsStore(() => _dir).Load();
        Assert.False(loaded.Thresholds);
        Assert.False(loaded.Resets);
        Assert.False(loaded.Enabled);
    }

    [Fact]
    public void FileFromBeforeKindTogglesReadsKindsAsEnabled()
    {
        // The master flag on, per-kind keys absent: they must read as enabled so a settings
        // file written before the toggles existed keeps the prior behavior.
        WriteSettings("""{ "NotificationsEnabled": true }""");
        var loaded = new NotificationSettingsStore(() => _dir).Load();
        Assert.True(loaded.Thresholds);
        Assert.True(loaded.Resets);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void SaveThenLoadRoundTrips(bool thresholds, bool resets)
    {
        var store = new NotificationSettingsStore(() => _dir);
        store.Save(new NotificationPreferences(thresholds, resets));
        Assert.Equal(new NotificationPreferences(thresholds, resets), store.Load());
    }

    [Fact]
    public void SaveWritesTheDerivedMasterFlagForOlderBuilds()
    {
        // A downgrade only understands NotificationsEnabled, so it must reflect whether any
        // kind is still on — otherwise rolling back would resurrect silenced alerts.
        var store = new NotificationSettingsStore(() => _dir);
        store.Save(new NotificationPreferences(false, false));
        Assert.Contains("\"NotificationsEnabled\": false", ReadSettings());

        store.Save(new NotificationPreferences(false, true));
        Assert.Contains("\"NotificationsEnabled\": true", ReadSettings());
    }

    [Fact]
    public void SavingLeavesOtherKeysIntact()
    {
        WriteSettings("""{ "EnabledTools": ["Cursor"], "Language": "ja" }""");

        new NotificationSettingsStore(() => _dir).Save(NotificationPreferences.Default with { Resets = false });

        // The notifications flags persisted, and the sibling keys survived the round-trip.
        Assert.False(new NotificationSettingsStore(() => _dir).Load().Resets);
        Assert.Equal(AppLanguage.Japanese, LanguageService.InitializeFromSettings(_dir));
        Assert.Equal(new[] { ToolKind.Cursor }, new ToolRegistryStore(() => _dir).Load());
    }

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(false, false, false)]
    public void EnabledIsDerivedFromTheKinds(bool thresholds, bool resets, bool expected)
        => Assert.Equal(expected, new NotificationPreferences(thresholds, resets).Enabled);

    private void WriteSettings(string json)
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "settings.json"), json);
    }

    private string ReadSettings() => File.ReadAllText(Path.Combine(_dir, "settings.json"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }
}
