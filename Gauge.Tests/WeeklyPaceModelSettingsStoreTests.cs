using Gauge.Localization;
using Gauge.Models;
using Gauge.Services;

namespace Gauge.Tests;

/// <summary>
/// Persistence validation for <see cref="WeeklyPaceModelSettingsStore"/>: an absent/malformed
/// or unrecognized value defaults to the uniform spread, and saving it must not clobber other
/// keys sharing <c>settings.json</c> (tool registration, UI language, notifications, view
/// mode, display basis, sparkline).
/// </summary>
public sealed class WeeklyPaceModelSettingsStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "GaugeTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void MissingFileDefaultsToUniform()
        => Assert.Equal(WeeklyPaceModel.Uniform, new WeeklyPaceModelSettingsStore(() => _dir).Load());

    [Fact]
    public void MalformedJsonDefaultsToUniform()
    {
        WriteSettings("{ not valid json");
        Assert.Equal(WeeklyPaceModel.Uniform, new WeeklyPaceModelSettingsStore(() => _dir).Load());
    }

    [Fact]
    public void UnknownValueDefaultsToUniform()
    {
        WriteSettings("""{ "WeeklyPaceModel": "fortnight" }""");
        Assert.Equal(WeeklyPaceModel.Uniform, new WeeklyPaceModelSettingsStore(() => _dir).Load());
    }

    [Theory]
    [InlineData("workdays", WeeklyPaceModel.WorkDays)]
    [InlineData("WorkDays", WeeklyPaceModel.WorkDays)]
    [InlineData("auto", WeeklyPaceModel.Automatic)]
    [InlineData("uniform", WeeklyPaceModel.Uniform)]
    public void ReadsTheDocumentedValuesCaseInsensitively(string value, WeeklyPaceModel expected)
    {
        WriteSettings($$"""{ "WeeklyPaceModel": "{{value}}" }""");
        Assert.Equal(expected, new WeeklyPaceModelSettingsStore(() => _dir).Load());
    }

    [Theory]
    [InlineData(WeeklyPaceModel.Uniform)]
    [InlineData(WeeklyPaceModel.WorkDays)]
    [InlineData(WeeklyPaceModel.Automatic)]
    public void SaveThenLoadRoundTrips(WeeklyPaceModel model)
    {
        var store = new WeeklyPaceModelSettingsStore(() => _dir);
        store.Save(model);
        Assert.Equal(model, store.Load());
    }

    [Fact]
    public void SavingLeavesOtherKeysIntact()
    {
        WriteSettings("""{ "EnabledTools": ["Cursor"], "Language": "ja", "NotificationsEnabled": false, "ViewMode": "gauge", "DisplayBasis": "remaining", "ShowSparkline": false }""");

        new WeeklyPaceModelSettingsStore(() => _dir).Save(WeeklyPaceModel.WorkDays);

        Assert.Equal(WeeklyPaceModel.WorkDays, new WeeklyPaceModelSettingsStore(() => _dir).Load());
        Assert.Equal(UsageViewMode.Gauge, new ViewModeSettingsStore(() => _dir).Load());
        Assert.Equal(UsageDisplayBasis.Remaining, new DisplayBasisSettingsStore(() => _dir).Load());
        Assert.False(new SparklineSettingsStore(() => _dir).Load());
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
