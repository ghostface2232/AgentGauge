using Gauge.Models;

namespace Gauge.Services;

/// <summary>
/// Persists the weekly pace model (uniform / work days / automatic) in
/// <c>%APPDATA%\Gauge\settings.json</c> via <see cref="AppSettingsFile"/>. The default is
/// the uniform spread — a missing/absent or unrecognized key reads as
/// <see cref="WeeklyPaceModel.Uniform"/>, so a settings file written before this option
/// existed keeps the original pace captions. Saving leaves other keys (tool registration,
/// UI language, notifications, view mode, display basis, sparkline) untouched.
/// </summary>
public sealed class WeeklyPaceModelSettingsStore
{
    private readonly Func<string> _directory;

    public WeeklyPaceModelSettingsStore(Func<string>? directory = null)
        => _directory = directory ?? (() => AppSettingsFile.DefaultDirectory);

    public WeeklyPaceModel Load() => Parse(AppSettingsFile.Load(_directory()).WeeklyPaceModel);

    public void Save(WeeklyPaceModel model) => _ = TrySave(model);

    /// <summary>
    /// The result-reporting form of <see cref="Save"/>. False means the choice did not reach
    /// disk — the caller must keep showing the previous model rather than a preference that
    /// would be gone on the next launch.
    /// </summary>
    public bool TrySave(WeeklyPaceModel model)
        => AppSettingsFile.TrySave(_directory(), dto => dto.WeeklyPaceModel = Serialize(model));

    internal static WeeklyPaceModel Parse(string? value) => value?.ToLowerInvariant() switch
    {
        "workdays" => WeeklyPaceModel.WorkDays,
        "auto" => WeeklyPaceModel.Automatic,
        _ => WeeklyPaceModel.Uniform,
    };

    internal static string Serialize(WeeklyPaceModel model) => model switch
    {
        WeeklyPaceModel.WorkDays => "workdays",
        WeeklyPaceModel.Automatic => "auto",
        _ => "uniform",
    };
}
