using Gauge.Services;

namespace Gauge.Tests;

/// <summary>
/// Persistence validation for <see cref="AutoUpdateSettingsStore"/>: an absent or unreadable
/// file defaults to on, and saving must not clobber other keys sharing <c>settings.json</c>.
/// </summary>
public sealed class AutoUpdateSettingsStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "GaugeTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void MissingFileDefaultsToOn()
        => Assert.True(new AutoUpdateSettingsStore(() => _dir).Load());

    [Fact]
    public void AbsentKeyDefaultsToOn()
    {
        WriteSettings("""{ "ViewMode": "bar" }""");
        Assert.True(new AutoUpdateSettingsStore(() => _dir).Load());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SaveThenLoadRoundTrips(bool enabled)
    {
        var store = new AutoUpdateSettingsStore(() => _dir);
        Assert.True(store.TrySave(enabled));
        Assert.Equal(enabled, store.Load());
    }

    [Fact]
    public void SavingLeavesOtherKeysIntact()
    {
        WriteSettings("""{ "ViewMode": "gauge", "ShowApiCost": true }""");

        new AutoUpdateSettingsStore(() => _dir).TrySave(false);

        Assert.True(new ApiCostSettingsStore(() => _dir).Load());
        Assert.Equal(Gauge.Models.UsageViewMode.Gauge, new ViewModeSettingsStore(() => _dir).Load());
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
