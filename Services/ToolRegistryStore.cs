using Gauge.Models;

namespace Gauge.Services;

/// <summary>Persists the set of tools the user has registered ("connected").</summary>
public interface IToolRegistryStore
{
    IReadOnlyCollection<ToolKind> Load();
    void Save(IReadOnlyCollection<ToolKind> enabled);
}

/// <summary>
/// Stores the registered tool set in <c>%APPDATA%\Gauge\settings.json</c> via
/// <see cref="AppSettingsFile"/>. Only the registration (which tools are shown) is
/// persisted here — never tokens or credentials — and saving leaves other keys (e.g. the
/// UI language) untouched. A missing/unreadable file falls back to the default set so
/// first run shows the established Claude Code + Codex experience.
/// </summary>
public sealed class ToolRegistryStore : IToolRegistryStore
{
    private static readonly IReadOnlyCollection<ToolKind> Default =
        new[] { ToolKind.ClaudeCode, ToolKind.Codex };

    private readonly Func<string> _directory;

    public ToolRegistryStore(Func<string>? directory = null)
        => _directory = directory ?? (() => AppSettingsFile.DefaultDirectory);

    public IReadOnlyCollection<ToolKind> Load()
    {
        if (AppSettingsFile.Load(_directory()).EnabledTools is not { Count: > 0 } names)
        {
            return Default;
        }

        var kinds = names
            .Select(name => Enum.TryParse<ToolKind>(name, out var kind) ? (ToolKind?)kind : null)
            .OfType<ToolKind>()
            .Distinct()
            .ToList();
        return kinds.Count > 0 ? kinds : Default;
    }

    public void Save(IReadOnlyCollection<ToolKind> enabled)
        => AppSettingsFile.Save(_directory(),
            dto => dto.EnabledTools = enabled.Select(kind => kind.ToString()).ToList());
}
