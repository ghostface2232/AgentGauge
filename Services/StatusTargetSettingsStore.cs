namespace Gauge.Services;

/// <summary>
/// Persists the status bar / tray icon target window in <c>%APPDATA%\Gauge\settings.json</c>
/// via <see cref="AppSettingsFile"/>. Empty string or null represents the default "Overall" (highest usage).
/// </summary>
public sealed class StatusTargetSettingsStore
{
    private readonly Func<string> _directory;

    public StatusTargetSettingsStore(Func<string>? directory = null)
        => _directory = directory ?? (() => AppSettingsFile.DefaultDirectory);

    public string Load() => AppSettingsFile.Load(_directory()).StatusTarget ?? string.Empty;

    public void Save(string? target) => _ = TrySave(target);

    public bool TrySave(string? target)
        => AppSettingsFile.TrySave(_directory(), dto => dto.StatusTarget = string.IsNullOrEmpty(target) ? null : target);
}
