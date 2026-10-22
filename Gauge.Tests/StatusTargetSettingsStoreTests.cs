using Gauge.Services;

namespace Gauge.Tests;

/// <summary>
/// Persistence validation for <see cref="StatusTargetSettingsStore"/>: an absent/malformed
/// value defaults to empty string ("Overall"), and saving it must not clobber other keys
/// sharing <c>settings.json</c>.
/// </summary>
public sealed class StatusTargetSettingsStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "GaugeTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void MissingFileDefaultsToEmpty()
        => Assert.Equal(string.Empty, new StatusTargetSettingsStore(() => _dir).Load());

    [Fact]
    public void MalformedJsonDefaultsToEmpty()
    {
        WriteSettings("{ not valid json");
        Assert.Equal(string.Empty, new StatusTargetSettingsStore(() => _dir).Load());
    }

    [Theory]
    [InlineData("")]
    [InlineData("ClaudeCode:fiveHour")]
    [InlineData("Codex:weekly")]
    public void SaveThenLoadRoundTrips(string target)
    {
        var store = new StatusTargetSettingsStore(() => _dir);
        store.Save(target);
        Assert.Equal(target, store.Load());
    }

    [Fact]
    public void SavingLeavesOtherKeysIntact()
    {
        WriteSettings("""{ "EnabledTools": ["Cursor"], "Language": "ja", "NotificationsEnabled": false, "ViewMode": "gauge" }""");

        new StatusTargetSettingsStore(() => _dir).Save("ClaudeCode:fiveHour");

        var loaded = AppSettingsFile.Load(_dir);
        Assert.Equal(["Cursor"], loaded.EnabledTools);
        Assert.Equal("ja", loaded.Language);
        Assert.False(loaded.NotificationsEnabled);
        Assert.Equal("gauge", loaded.ViewMode);
        Assert.Equal("ClaudeCode:fiveHour", loaded.StatusTarget);
    }

    private void WriteSettings(string json)
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "settings.json"), json);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
        }
        catch
        {
            // best-effort test cleanup
        }
    }
}
