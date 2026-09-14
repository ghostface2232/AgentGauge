using Gauge.Models;

namespace Gauge.Services;

/// <summary>
/// Persists the card view mode (bar vs gauge) in <c>%APPDATA%\Gauge\settings.json</c> via
/// <see cref="AppSettingsFile"/>. The default is the bar layout — a missing/absent or
/// unrecognized key reads as <see cref="UsageViewMode.Bar"/>, so a settings file written
/// before this option existed keeps the original presentation. Saving leaves other keys
/// (tool registration, UI language, notifications, display basis) untouched.
/// </summary>
public sealed class ViewModeSettingsStore
{
    private readonly Func<string> _directory;

    public ViewModeSettingsStore(Func<string>? directory = null)
        => _directory = directory ?? (() => AppSettingsFile.DefaultDirectory);

    public UsageViewMode Load() =>
        string.Equals(AppSettingsFile.Load(_directory()).ViewMode, "gauge", StringComparison.OrdinalIgnoreCase)
            ? UsageViewMode.Gauge
            : UsageViewMode.Bar;

    public void Save(UsageViewMode mode) => _ = TrySave(mode);

    /// <summary>
    /// The result-reporting form of <see cref="Save"/>. False means the choice did not reach
    /// disk — the caller must keep showing the previous mode rather than a preference that
    /// would be gone on the next launch.
    /// </summary>
    public bool TrySave(UsageViewMode mode)
        => AppSettingsFile.TrySave(_directory(), dto => dto.ViewMode = mode == UsageViewMode.Gauge ? "gauge" : "bar");
}
