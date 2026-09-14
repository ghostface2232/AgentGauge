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

    public void Save(bool show) => _ = TrySave(show);

    /// <summary>
    /// The result-reporting form of <see cref="Save"/>. False means the choice did not reach
    /// disk — the caller must keep showing the previous state rather than a preference that
    /// would be gone on the next launch.
    /// </summary>
    public bool TrySave(bool show)
        => AppSettingsFile.TrySave(_directory(), dto => dto.ShowSparkline = show);
}
