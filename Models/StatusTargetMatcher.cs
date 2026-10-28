namespace Gauge.Models;

/// <summary>
/// Helpers for matching status bar / tray icon target strings across tools.
/// Handles cross-platform differences, legacy aliases ("Claude Code" vs "Claude"),
/// and case-insensitivity.
/// </summary>
public static class StatusTargetMatcher
{
    /// <summary>
    /// The tool a persisted target names. Targets used to name one window
    /// ("Codex:weekly"); the tray now shows a tool's 5-hour and weekly windows together, so
    /// such a value is read as its tool rather than discarded.
    /// </summary>
    public static string ToolPart(string? target)
    {
        if (IsOverall(target)) return string.Empty;
        var colon = target!.IndexOf(':');
        return colon < 0 ? target : target[..colon];
    }

    public static bool IsOverall(string? target)
        => string.IsNullOrEmpty(target) || string.Equals(target, "all", StringComparison.OrdinalIgnoreCase);

    public static bool Matches(string candidateTarget, string requestedTarget)
    {
        var candidate = ToolPart(candidateTarget);
        var requested = ToolPart(requestedTarget);
        if (candidate.Length == 0 || requested.Length == 0)
        {
            return candidate.Length == 0 && requested.Length == 0;
        }

        return ToolsMatch(candidate, requested);
    }

    /// <summary>The catalog entry a target or tool name refers to, under any of its aliases.</summary>
    public static ToolDescriptor? Descriptor(string tool)
    {
        var current = ToolCatalog.CurrentDisplayName(tool);
        return ToolCatalog.All.FirstOrDefault(d =>
            string.Equals(d.DisplayName, current, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(d.Kind.ToString(), tool, StringComparison.OrdinalIgnoreCase));
    }

    public static bool ToolsMatch(string toolA, string toolB)
    {
        if (string.Equals(toolA, toolB, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var currentA = ToolCatalog.CurrentDisplayName(toolA);
        var currentB = ToolCatalog.CurrentDisplayName(toolB);
        if (string.Equals(currentA, currentB, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var descA = Descriptor(toolA);
        var descB = Descriptor(toolB);
        return descA != null && descB != null && descA.Kind == descB.Kind;
    }
}
