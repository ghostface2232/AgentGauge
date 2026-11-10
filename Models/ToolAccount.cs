using System.Text.RegularExpressions;
using System.Text.Json.Serialization;

namespace Gauge.Models;

/// <summary>A named CLI account in addition to the tool's existing default login.</summary>
public sealed record ToolAccount(Guid Id, ToolKind Tool, string Label)
{
    internal const char KeySeparator = '#';
    public const int LabelMaxLength = 40;

    [JsonIgnore] public string Key => $"{Tool}{KeySeparator}{Id:N}";
    [JsonIgnore] public string DisplayName => $"{ToolCatalog.For(Tool).DisplayName} · {Label}";

    public static bool SupportsAdditionalAccounts(ToolKind tool) =>
        tool is ToolKind.ClaudeCode or ToolKind.Codex or ToolKind.GitHubCopilot;

    public static bool IsValidLabel(ToolKind tool, string? label) =>
        label is { Length: > 0 and <= LabelMaxLength }
        && !string.IsNullOrWhiteSpace(label)
        && !label.Any(char.IsControl)
        && (tool != ToolKind.GitHubCopilot || Regex.IsMatch(label, @"\A[a-zA-Z0-9][a-zA-Z0-9_-]*\z"));
}
