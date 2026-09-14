using Gauge.Localization;
using Gauge.Models;
using Gauge.Services;

namespace Gauge.Tests;

/// <summary>
/// Persistence validation for <see cref="DisplayBasisSettingsStore"/>: an absent/malformed or
/// unrecognized value defaults to the used basis, and saving it must not clobber other keys
/// sharing <c>settings.json</c> (tool registration, UI language, notifications, view mode).
/// </summary>
public sealed class DisplayBasisSettingsStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "GaugeTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void MissingFileDefaultsToUsed()
        => Assert.Equal(UsageDisplayBasis.Used, new DisplayBasisSettingsStore(() => _dir).Load());

    [Fact]
    public void MalformedJsonDefaultsToUsed()
    {
        WriteSettings("{ not valid json");
        Assert.Equal(UsageDisplayBasis.Used, new DisplayBasisSettingsStore(() => _dir).Load());
    }

    [Fact]
    public void UnknownValueDefaultsToUsed()
    {
        WriteSettings("""{ "DisplayBasis": "left" }""");
        Assert.Equal(UsageDisplayBasis.Used, new DisplayBasisSettingsStore(() => _dir).Load());
    }

    [Theory]
    [InlineData(UsageDisplayBasis.Used)]
    [InlineData(UsageDisplayBasis.Remaining)]
    public void SaveThenLoadRoundTrips(UsageDisplayBasis basis)
    {
        var store = new DisplayBasisSettingsStore(() => _dir);
        store.Save(basis);
        Assert.Equal(basis, store.Load());
    }

    [Fact]
    public void SavingOverAnUnreadableFileIsRefusedRatherThanRewritingItFromDefaults()
    {
        const string corrupt = """{ "EnabledTools": ["Cursor"], "Language": "ja", """;
        WriteSettings(corrupt);

        Assert.False(new DisplayBasisSettingsStore(() => _dir).TrySave(UsageDisplayBasis.Remaining));
        Assert.Equal(corrupt, File.ReadAllText(Path.Combine(_dir, "settings.json")));
    }

    [Fact]
    public void SavingLeavesOtherKeysIntact()
    {
        WriteSettings("""{ "EnabledTools": ["Cursor"], "Language": "ja", "NotificationsEnabled": false, "ViewMode": "gauge" }""");

        new DisplayBasisSettingsStore(() => _dir).Save(UsageDisplayBasis.Remaining);

        Assert.Equal(UsageDisplayBasis.Remaining, new DisplayBasisSettingsStore(() => _dir).Load());
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
