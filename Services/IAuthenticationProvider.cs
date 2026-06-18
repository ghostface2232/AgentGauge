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
}
