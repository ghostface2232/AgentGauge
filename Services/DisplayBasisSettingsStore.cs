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

    public void Save(UsageDisplayBasis basis) => _ = TrySave(basis);

    /// <summary>
    /// The result-reporting form of <see cref="Save"/>. False means the choice did not reach
    /// disk — the caller must keep showing the previous basis rather than a preference that
    /// would be gone on the next launch.
    /// </summary>
    public bool TrySave(UsageDisplayBasis basis)
        => AppSettingsFile.TrySave(_directory(), dto => dto.DisplayBasis = basis == UsageDisplayBasis.Remaining ? "remaining" : "used");
}
