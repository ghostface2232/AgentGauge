namespace Gauge.Models;

public enum ToolKind
{
    ClaudeCode,
    Codex,
    Cursor,
    // Antigravity reads its rich 5h/weekly quota from the IDE's local language server
    // (attach), or from an engine Gauge launches itself when the IDE is closed (delegate).
    Antigravity,
    // GitHub Copilot exposes its monthly quota (chat / completions / premium requests) via
    // GitHub's internal copilot_internal/user endpoint, read with the GitHub OAuth token a
    // local client already stores (the gh CLI, or a github-copilot apps.json file).
    GitHubCopilot,
}

public enum CredentialOwner
{
    GaugeManaged,
    CliLocal,
}

public enum CredentialSource
{
    None,
    GaugeManaged,
    CliLocal,
}

public enum CredentialReadStatus
{
    Available,
    Missing,
    Invalid,
}

public sealed record ToolCredential
{
    public required ToolKind Tool { get; init; }
    public required CredentialOwner Owner { get; init; }
    public required CredentialSource Source { get; init; }
    public required string AccessToken { get; init; }
    public string? AccountId { get; init; }
    public string? Plan { get; init; }
    public DateTimeOffset? ExpiresAt { get; init; }

    // OAuth refresh material — populated only for tools whose access token expires
    // quickly and must be re-minted in memory before each usage call (e.g. Antigravity
    // via Google CloudCode). Null for simple Bearer-token tools. Gauge never writes
    // these back to disk; the refresh happens in-memory only.
    public string? RefreshToken { get; init; }
    public string? ClientId { get; init; }
    public string? ClientSecret { get; init; }
    public string? ProjectId { get; init; }
}

public sealed record CredentialReadResult
{
    public required ToolKind Tool { get; init; }
    public required CredentialReadStatus Status { get; init; }
    public ToolCredential? Credential { get; init; }
    public string? Message { get; init; }
}

public enum AuthenticationStatus
{
    Available,
    Missing,
    Invalid,
    LoginRunning,
    LoginFailed,
}

public sealed record AuthenticationState
{
    public required ToolKind Tool { get; init; }
    public required string ToolName { get; init; }
    public required AuthenticationStatus Status { get; init; }
    public required CredentialSource Source { get; init; }
    public required string Message { get; init; }
    public bool IsLoginRunning => Status == AuthenticationStatus.LoginRunning;
}
