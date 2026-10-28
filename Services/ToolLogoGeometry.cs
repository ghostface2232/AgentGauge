using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Gauge.Models;

namespace Gauge.Services;

/// <summary>
/// The path data of <c>Assets\ToolLogos\{kind}.svg</c> as a XAML geometry string, so a card
/// header can draw the same single-path logo the tray ring uses, filled with a theme brush.
/// </summary>
public static partial class ToolLogoGeometry
{
    private static readonly ConcurrentDictionary<ToolKind, string?> Cache = new();

    /// <summary>Null when the file is missing or has no path, in which case the card shows no logo.</summary>
    public static string? For(ToolKind kind) => Cache.GetOrAdd(kind, Load);

    private static string? Load(ToolKind kind)
    {
        var file = Path.Combine(AppContext.BaseDirectory, "Assets", "ToolLogos", $"{kind}.svg");
        try
        {
            if (!File.Exists(file)) return null;
            var svg = File.ReadAllText(file);
            var data = PathDataPattern().Match(svg);
            if (!data.Success) return null;
            // XAML's path mini-language fills even-odd unless it starts with "F1".
            return svg.Contains("evenodd", StringComparison.Ordinal) ? data.Groups[1].Value : "F1 " + data.Groups[1].Value;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DiagnosticsLog.Write("app", $"Tool logo for {kind} could not be read: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    [GeneratedRegex(@"\sd=""([^""]+)""")]
    private static partial Regex PathDataPattern();
}
