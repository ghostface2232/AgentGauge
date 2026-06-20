using Gauge.Models;

namespace Gauge.Services;

public interface IAuthenticationProvider
{
    ToolKind Tool { get; }
    AuthenticationState State { get; }
    bool IsLoginRunning { get; }
    event EventHandler<AuthenticationState>? StateChanged;
    Task<AuthenticationState> RefreshStateAsync(CancellationToken cancellationToken = default);
    Task<AuthenticationState> LoginAsync(CancellationToken cancellationToken = default);
    void ReportInvalidCredentials();

    /// <summary>
    /// Clears a prior server-side rejection after the token has been accepted again (a
    /// usage fetch succeeded). Without this, a transient rejection on an otherwise-valid
    /// token keeps the card marked "signed out" indefinitely, because the rejection is
    /// keyed by token fingerprint and that fingerprint doesn't change until the CLI rotates
    /// the token.
    /// </summary>
    void ReportCredentialsAccepted();
}
