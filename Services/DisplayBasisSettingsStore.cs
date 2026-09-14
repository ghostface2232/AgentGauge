using Gauge.Models;

namespace Gauge.Services;

/// <summary>
/// Persists the usage display basis (used vs remaining) in <c>%APPDATA%\Gauge\settings.json</c>
/// via <see cref="AppSettingsFile"/>. The default is the used basis — a missing/absent or
/// unrecognized key reads as <see cref="UsageDisplayBasis.Used"/>, so a settings file written
/// before this option existed keeps the original presentation. Saving leaves other keys
/// (tool registration, UI language, notifications, view mode) untouched.
/// </summary>
public sealed class DisplayBasisSettingsStore
{
    private readonly Func<string> _directory;

    public DisplayBasisSettingsStore(Func<string>? directory = null)
        => _directory = directory ?? (() => AppSettingsFile.DefaultDirectory);

    public UsageDisplayBasis Load() =>
        string.Equals(AppSettingsFile.Load(_directory()).DisplayBasis, "remaining", StringComparison.OrdinalIgnoreCase)
            ? UsageDisplayBasis.Remaining
            : UsageDisplayBasis.Used;

    public void Save(UsageDisplayBasis basis)
        => AppSettingsFile.Save(_directory(), dto => dto.DisplayBasis = basis == UsageDisplayBasis.Remaining ? "remaining" : "used");
}
