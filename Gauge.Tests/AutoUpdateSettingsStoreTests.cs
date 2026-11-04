using Gauge.Services;

namespace Gauge.Tests;

/// <summary>
/// Persistence validation for <see cref="AutoUpdateSettingsStore"/>: an absent file defaults
/// to on, an unreadable one to off (installing is not undoable), and saving must not clobber
/// other keys sharing <c>settings.json</c>.
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

    [Fact]
    public void UnreadableFileReadsAsOffSoAMutedUserIsNotInstalledOver()
    {
        WriteSettings("{ not json");
        Assert.False(new AutoUpdateSettingsStore(() => _dir).Load());
    }

    [Fact]
    public void ALockedFileReadsAsOff()
    {
        WriteSettings("""{ "AutoUpdate": false }""");
        using var held = new FileStream(
            Path.Combine(_dir, "settings.json"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        Assert.False(new AutoUpdateSettingsStore(() => _dir).Load());
    }

    [Fact]
    public void AnAttemptIsRememberedPerVersion()
    {
        var store = new AutoUpdateSettingsStore(() => _dir);
        Assert.False(store.WasAttempted(new Version(1, 2, 3)));

        Assert.True(store.TryRecordAttempt(new Version(1, 2, 3)));

        Assert.True(store.WasAttempted(new Version(1, 2, 3)));
        Assert.False(store.WasAttempted(new Version(1, 2, 4)));
    }

    [Fact]
    public void AnUnreadableFileRefusesTheAttemptAndCountsAsAttempted()
    {
        WriteSettings("{ not json");
        var store = new AutoUpdateSettingsStore(() => _dir);

        Assert.False(store.TryRecordAttempt(new Version(1, 2, 3)));
        Assert.True(store.WasAttempted(new Version(1, 2, 3)));
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
