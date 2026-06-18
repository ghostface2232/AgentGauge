namespace Gauge.Models;

public enum ToolKind
{
    ClaudeCode,
    Codex,
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
