using Gauge.Models;
using Gauge.Services;
using Gauge.ViewModels;

namespace Gauge.Tests;

public sealed class SettingsViewModelTests
{
    [Theory]
    [InlineData(AuthenticationStatus.Missing, "로그인", "더 정확한 사용량")]
    [InlineData(AuthenticationStatus.Available, "계정 전환", "로그인됨")]
    [InlineData(AuthenticationStatus.LoginRunning, "로그인 중…", "완료")]
    public void CardReflectsAuthenticationState(AuthenticationStatus status, string button, string messagePart)
    {
        var provider = new FakeAuthenticationProvider(State(status, messagePart));
        var card = new AuthenticationCardViewModel(provider);
        Assert.Equal(button, card.LoginButtonText);
        Assert.Contains(messagePart, card.StatusText);
        Assert.Equal(status == AuthenticationStatus.LoginRunning, card.IsLoginRunning);
    }

    private static AuthenticationState State(AuthenticationStatus status, string message) => new()
    {
        Tool = ToolKind.Codex, ToolName = "Codex", Status = status,
        Source = status == AuthenticationStatus.Available ? CredentialSource.CliLocal : CredentialSource.None,
        Message = message,
    };

    private sealed class FakeAuthenticationProvider(AuthenticationState state) : IAuthenticationProvider
    {
        public ToolKind Tool => ToolKind.Codex;
        public AuthenticationState State { get; private set; } = state;
        public bool IsLoginRunning => State.IsLoginRunning;
        public event EventHandler<AuthenticationState>? StateChanged { add { } remove { } }
        public Task<AuthenticationState> RefreshStateAsync(CancellationToken cancellationToken = default) => Task.FromResult(State);
        public Task<AuthenticationState> LoginAsync(CancellationToken cancellationToken = default) => Task.FromResult(State);
        public void ReportInvalidCredentials() { }
        public void ReportCredentialsAccepted() { }
    }
}
