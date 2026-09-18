namespace Gauge.Services;

/// <summary>
/// Persists whether cards show the API-equivalent cost estimate in
/// <c>%APPDATA%\Gauge\settings.json</c> via <see cref="AppSettingsFile"/>. The default is
/// off — a missing/absent key reads as <c>false</c> — because turning it on is what lets
/// AgentGauge read the CLIs' local session logs, and that is the user's call to make, never
/// a surprise on first launch. Saving leaves other keys untouched.
/// </summary>
public sealed class ApiCostSettingsStore
{
    private readonly Func<string> _directory;

    public ApiCostSettingsStore(Func<string>? directory = null)
        => _directory = directory ?? (() => AppSettingsFile.DefaultDirectory);

    public bool Load() => AppSettingsFile.Load(_directory()).ShowApiCost ?? false;

    public void Save(bool show) => _ = TrySave(show);

    /// <summary>
    /// The result-reporting form of <see cref="Save"/>. False means the choice did not reach
    /// disk — the caller must keep showing the previous state rather than a preference that
    /// would be gone on the next launch.
    /// </summary>
    public bool TrySave(bool show)
        => AppSettingsFile.TrySave(_directory(), dto => dto.ShowApiCost = show);
}
