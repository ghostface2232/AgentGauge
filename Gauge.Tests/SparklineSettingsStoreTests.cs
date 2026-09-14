using Gauge.Localization;
using Gauge.Models;
using Gauge.Services;

namespace Gauge.Tests;

/// <summary>
/// Persistence validation for <see cref="SparklineSettingsStore"/>: an absent/malformed file
/// defaults to shown, and saving must not clobber other keys sharing <c>settings.json</c>
/// (tool registration, UI language, notifications, view mode, display basis).
/// </summary>
public sealed class SparklineSettingsStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "GaugeTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void MissingFileDefaultsToShown()
        => Assert.True(new SparklineSettingsStore(() => _dir).Load());

    [Fact]
    public void MalformedJsonDefaultsToShown()
    {
        WriteSettings("{ not valid json");
        Assert.True(new SparklineSettingsStore(() => _dir).Load());
    }

    [Fact]
    public void AbsentKeyDefaultsToShown()
    {
        WriteSettings("""{ "ViewMode": "bar" }""");
        Assert.True(new SparklineSettingsStore(() => _dir).Load());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SaveThenLoadRoundTrips(bool show)
    {
        var store = new SparklineSettingsStore(() => _dir);
        store.Save(show);
        Assert.Equal(show, store.Load());
    }

    [Fact]
    public void SavingOverAnUnreadableFileIsRefusedRatherThanRewritingItFromDefaults()
    {
        const string corrupt = """{ "EnabledTools": ["Cursor"], "Language": "ja", """;
        WriteSettings(corrupt);

        Assert.False(new SparklineSettingsStore(() => _dir).TrySave(false));
        Assert.Equal(corrupt, File.ReadAllText(Path.Combine(_dir, "settings.json")));
    }

    [Fact]
    public void SavingLeavesOtherKeysIntact()
    {
        WriteSettings("""{ "EnabledTools": ["Cursor"], "Language": "ja", "NotificationsEnabled": false, "ViewMode": "gauge", "DisplayBasis": "remaining" }""");

        new SparklineSettingsStore(() => _dir).Save(false);

        Assert.False(new SparklineSettingsStore(() => _dir).Load());
        Assert.Equal(UsageViewMode.Gauge, new ViewModeSettingsStore(() => _dir).Load());
        Assert.Equal(UsageDisplayBasis.Remaining, new DisplayBasisSettingsStore(() => _dir).Load());
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
