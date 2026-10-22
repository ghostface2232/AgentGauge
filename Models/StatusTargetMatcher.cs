namespace Gauge.Models;

/// <summary>
/// Helpers for matching status bar / tray icon target strings across tools and windows.
/// Handles cross-platform differences, legacy aliases ("Claude Code" vs "Claude"),
/// and case-insensitivity.
/// </summary>
public static class StatusTargetMatcher
{
    public static bool Matches(string candidateTarget, string requestedTarget)
    {
        var overallRequested = string.IsNullOrEmpty(requestedTarget)
            || string.Equals(requestedTarget, "all", StringComparison.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(candidateTarget) && overallRequested)
        {
            return true;
        }

        if (string.IsNullOrEmpty(candidateTarget) || overallRequested)
        {
            return false;
        }

        var candidateParts = candidateTarget.Split(':', 2);
        var requestedParts = requestedTarget.Split(':', 2);
        if (candidateParts.Length != 2 || requestedParts.Length != 2)
        {
            return false;
        }

        return ToolsMatch(candidateParts[0], requestedParts[0])
            && string.Equals(candidateParts[1], requestedParts[1], StringComparison.OrdinalIgnoreCase);
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

        var descA = ToolCatalog.All.FirstOrDefault(d =>
            string.Equals(d.DisplayName, currentA, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(d.Kind.ToString(), toolA, StringComparison.OrdinalIgnoreCase));
        var descB = ToolCatalog.All.FirstOrDefault(d =>
            string.Equals(d.DisplayName, currentB, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(d.Kind.ToString(), toolB, StringComparison.OrdinalIgnoreCase));

        return descA != null && descB != null && descA.Kind == descB.Kind;
    }
}
