namespace Gauge.Models;

/// <summary>How a tool's "login" action is performed from its settings card.</summary>
public enum LoginKind
{
    /// <summary>Run the tool's official CLI login command (e.g. <c>claude /login</c>).</summary>
    CliCommand,

    /// <summary>
    /// The tool has no headless CLI login; the user signs in via its app/IDE. The card
    /// shows guidance and Gauge simply detects the credential once it appears.
    /// </summary>
    GuidanceOnly,
}

/// <summary>
/// Per-tool data that is the same wherever the tool is referenced: how its card is
/// labelled and how its official CLI is invoked for login. Keeping it here means a new
/// tool needs only a <see cref="ToolKind"/> case plus one entry in
/// <see cref="ToolCatalog"/> — the display name and login command no longer have to be
/// repeated (and kept in sync) across the providers, the auth state, and the UI.
/// </summary>
public sealed record ToolDescriptor
{
    public required ToolKind Kind { get; init; }

    /// <summary>Card label, e.g. "Claude Code", "Codex".</summary>
    public required string DisplayName { get; init; }

    /// <summary>Executable that performs an interactive login, e.g. "claude", "codex".</summary>
    public required string LoginCommand { get; init; }

    /// <summary>Arguments passed to <see cref="LoginCommand"/>, e.g. "/login", "login".</summary>
    public required string LoginArguments { get; init; }

    /// <summary>How the settings card's login action behaves. Defaults to running the CLI.</summary>
    public LoginKind LoginKind { get; init; } = LoginKind.CliCommand;

    /// <summary>
    /// Localization key for the short instruction shown on the card when
    /// <see cref="LoginKind"/> is <see cref="LoginKind.GuidanceOnly"/> (the tool is signed
    /// in elsewhere). Null otherwise. Resolve via <c>Loc.Get</c> at display time — it is a
    /// key, not display text, because this catalog is built before the language is set.
    /// </summary>
    public string? LoginGuidance { get; init; }
}

/// <summary>The single source of truth for every tool Gauge knows about.</summary>
public static class ToolCatalog
{
    public static readonly ToolDescriptor ClaudeCode = new()
    {
        Kind = ToolKind.ClaudeCode,
        DisplayName = "Claude Code",
        LoginCommand = "claude",
        LoginArguments = "/login",
    };

    public static readonly ToolDescriptor Codex = new()
    {
        Kind = ToolKind.Codex,
        DisplayName = "Codex",
        LoginCommand = "codex",
        LoginArguments = "login",
    };

    public static readonly ToolDescriptor Cursor = new()
    {
        Kind = ToolKind.Cursor,
        DisplayName = "Cursor",
        // No CLI login: the user signs into the Cursor app; Gauge reads its local token.
        LoginCommand = "",
        LoginArguments = "",
        LoginKind = LoginKind.GuidanceOnly,
        LoginGuidance = "Guidance_Cursor",
    };

    public static readonly ToolDescriptor Antigravity = new()
    {
        Kind = ToolKind.Antigravity,
        DisplayName = "Antigravity",
        // No CLI login: the user signs into the Antigravity IDE; Gauge reads quota from its
        // local language server. The card's sign-in guidance text is wired up with the rest of
        // the settings UX.
        LoginCommand = "",
        LoginArguments = "",
        LoginKind = LoginKind.GuidanceOnly,
    };

    public static readonly ToolDescriptor GitHubCopilot = new()
    {
        Kind = ToolKind.GitHubCopilot,
        DisplayName = "GitHub Copilot",
        // Sign-in is delegated to the GitHub CLI's device-flow login. Gauge then reads the
        // OAuth token gh stores (or a github-copilot apps.json file) — it never logs in itself.
        LoginCommand = "gh",
        LoginArguments = "auth login",
    };

    /// <summary>Declaration order is the order tools are shown in the UI.</summary>
    public static readonly IReadOnlyList<ToolDescriptor> All = new[] { ClaudeCode, Codex, Cursor, Antigravity, GitHubCopilot };

    private static readonly IReadOnlyDictionary<ToolKind, ToolDescriptor> ByKind =
        All.ToDictionary(descriptor => descriptor.Kind);

    public static ToolDescriptor For(ToolKind kind) => ByKind[kind];
}
