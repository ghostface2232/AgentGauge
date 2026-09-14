namespace Gauge.Services;

/// <summary>
/// Persists whether bar rows show the burndown sparkline in
/// <c>%APPDATA%\Gauge\settings.json</c> via <see cref="AppSettingsFile"/>. The default is
/// shown — a missing/absent key reads as <c>true</c>, so a settings file written before this
/// option existed keeps the original presentation. Saving leaves other keys (tool
/// registration, UI language, notifications, view mode, display basis) untouched.
/// </summary>
public sealed class SparklineSettingsStore
{
    private readonly Func<string> _directory;

    public SparklineSettingsStore(Func<string>? directory = null)
        => _directory = directory ?? (() => AppSettingsFile.DefaultDirectory);

    public bool Load() => AppSettingsFile.Load(_directory()).ShowSparkline ?? true;

    public void Save(bool show)
        => AppSettingsFile.Save(_directory(), dto => dto.ShowSparkline = show);
}
