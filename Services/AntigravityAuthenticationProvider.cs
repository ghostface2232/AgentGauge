using Gauge.Localization;
using Gauge.Models;

namespace Gauge.Services;

/// <summary>
/// Settings-card auth state for Antigravity. Unlike the CLI tools, Gauge has no Antigravity
/// credential to read — sign-in is owned entirely by the IDE's own OAuth — so the card cannot
/// (and must not) claim "not signed in" by the absence of a token. Instead it starts with a
/// neutral description of where the data comes from and flips to "signed in" once a usage read
/// actually succeeds (<see cref="ReportCredentialsAccepted"/>), which is the only reliable proof
/// the app is signed in. A failed read is left as-is rather than misreported as signed out, since
/// it can't be told apart from the IDE merely being closed.
/// </summary>
public sealed class AntigravityAuthenticationProvider : IAuthenticationProvider
{
    private static readonly ToolDescriptor Descriptor = ToolCatalog.For(ToolKind.Antigravity);

    public AntigravityAuthenticationProvider() => State = Build(signedIn: false);

    public ToolKind Tool => ToolKind.Antigravity;
    public AuthenticationState State { get; private set; }
    public bool IsLoginRunning => false;
    public event EventHandler<AuthenticationState>? StateChanged;

    public Task<AuthenticationState> RefreshStateAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(State);

    public Task<AuthenticationState> LoginAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(State);

    public void ReportInvalidCredentials()
    {
        // No headless sign-out signal exists; keep the last state rather than guessing.
    }

    public void ReportCredentialsAccepted()
    {
        if (State.Status != AuthenticationStatus.Available)
        {
            State = Build(signedIn: true);
            StateChanged?.Invoke(this, State);
        }
    }

    private static AuthenticationState Build(bool signedIn) => new()
    {
        Tool = ToolKind.Antigravity,
        ToolName = Descriptor.DisplayName,
        Status = signedIn ? AuthenticationStatus.Available : AuthenticationStatus.Missing,
        Source = signedIn ? CredentialSource.CliLocal : CredentialSource.None,
        Message = signedIn ? Loc.Get("Auth_SignedIn") : Loc.Get("Auth_Antigravity"),
    };
}
