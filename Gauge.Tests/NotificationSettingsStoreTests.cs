using Gauge.Localization;
using Gauge.Models;
using Gauge.Services;

namespace Gauge.Tests;

/// <summary>
/// Persistence validation for <see cref="NotificationSettingsStore"/>: an absent/malformed
/// file defaults to enabled, and saving the flag must not clobber other keys sharing
/// <c>settings.json</c> (the tool registration and UI language).
/// </summary>
public sealed class NotificationSettingsStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "GaugeTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void MissingFileDefaultsToEnabled()
        => Assert.True(new NotificationSettingsStore(() => _dir).Load());

    [Fact]
    public void MalformedJsonDefaultsToEnabled()
    {
        WriteSettings("{ not valid json");
        Assert.True(new NotificationSettingsStore(() => _dir).Load());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SaveThenLoadRoundTrips(bool enabled)
    {
        var store = new NotificationSettingsStore(() => _dir);
        store.Save(enabled);
        Assert.Equal(enabled, store.Load());
    }

    [Fact]
    public void SavingLeavesOtherKeysIntact()
    {
        WriteSettings("""{ "EnabledTools": ["Cursor"], "Language": "ja" }""");

        new NotificationSettingsStore(() => _dir).Save(false);

        // The notifications flag persisted, and the sibling keys survived the round-trip.
        Assert.False(new NotificationSettingsStore(() => _dir).Load());
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
