using Gauge.Models;
using Gauge.Services;

namespace Gauge.Tests;

public sealed class AuthenticationProviderTests
{
    [Fact]
    public async Task MissingCliReportsActionableFailure()
    {
        var provider = Create(new FakeSource(), new FakeLocator(null), new FakeRunner());
        var state = await provider.LoginAsync();
        Assert.Equal(AuthenticationStatus.LoginFailed, state.Status);
        Assert.Contains("codex login", state.Message);
    }

    [Fact]
    public async Task SuccessfulLoginRereadsCredentialAndRejectsDuplicateClick()
    {
        var source = new FakeSource();
        var runner = new FakeRunner { Wait = new TaskCompletionSource<CliProcessResult>() };
        var provider = Create(source, new FakeLocator("codex.cmd"), runner);
        var first = provider.LoginAsync();
        await Task.Yield();
        var duplicate = await provider.LoginAsync();
        Assert.Equal(AuthenticationStatus.LoginRunning, duplicate.Status);

        source.Result = Available();
        runner.Wait.SetResult(new CliProcessResult(0, false));
        Assert.Equal(AuthenticationStatus.Available, (await first).Status);
        Assert.Equal(1, runner.CallCount);
    }

    [Theory]
    [InlineData(2, false)]
    [InlineData(-1, true)]
    public async Task FailureAndTimeoutBecomeLoginFailed(int exitCode, bool timedOut)
    {
        var runner = new FakeRunner { Result = new CliProcessResult(exitCode, timedOut) };
        var state = await Create(new FakeSource(), new FakeLocator("codex.exe"), runner).LoginAsync();
        Assert.Equal(AuthenticationStatus.LoginFailed, state.Status);
    }

    [Fact]
    public async Task CancellationBecomesClearFailedState()
    {
        var runner = new CancellingRunner();
        var state = await Create(new FakeSource(), new FakeLocator("codex.exe"), runner).LoginAsync();
        Assert.Equal(AuthenticationStatus.LoginFailed, state.Status);
        Assert.Contains("취소", state.Message);
    }

    [Fact]
    public async Task RejectedCredentialStaysInvalidWhenSettingsRefreshes()
    {
        var source = new FakeSource { Result = Available() };
        var provider = Create(source, new FakeLocator("codex.exe"), new FakeRunner());
        await provider.RefreshStateAsync();
        provider.ReportInvalidCredentials();

        var refreshed = await provider.RefreshStateAsync();
        Assert.Equal(AuthenticationStatus.Invalid, refreshed.Status);
    }

    [Fact]
    public async Task AcceptedCredentialClearsStickyRejectionAndRecovers()
    {
        var source = new FakeSource { Result = Available() };
        var provider = Create(source, new FakeLocator("codex.exe"), new FakeRunner());
        await provider.RefreshStateAsync();
        provider.ReportInvalidCredentials();
        Assert.Equal(AuthenticationStatus.Invalid, provider.State.Status);

        // A subsequent successful fetch reports the token accepted: the card flips back to
        // signed-in immediately and a later settings refresh keeps it that way (the sticky
        // rejection is gone, even though the token fingerprint never changed).
        provider.ReportCredentialsAccepted();
        Assert.Equal(AuthenticationStatus.Available, provider.State.Status);
        Assert.Equal(AuthenticationStatus.Available, (await provider.RefreshStateAsync()).Status);
    }

    private static CliAuthenticationProvider Create(ICredentialSource source, ICliLocator locator, ICliProcessRunner runner)
        => new(ToolKind.Codex, source, locator, runner);

    private static CredentialReadResult Available() => new()
    {
        Tool = ToolKind.Codex, Status = CredentialReadStatus.Available,
        Credential = new ToolCredential { Tool = ToolKind.Codex, Owner = CredentialOwner.CliLocal,
            Source = CredentialSource.CliLocal, AccessToken = "secret" },
    };

    private sealed class FakeSource : ICredentialSource
    {
        public CredentialReadResult Result { get; set; } = new() { Tool = ToolKind.Codex, Status = CredentialReadStatus.Missing };
        public CredentialOwner Owner => CredentialOwner.CliLocal;
        public CredentialSource Source => CredentialSource.CliLocal;
        public Task<CredentialReadResult> ReadAsync(ToolKind tool, CancellationToken cancellationToken = default) => Task.FromResult(Result);
    }
    private sealed record FakeLocator(string? Path) : ICliLocator { public string? Find(string commandName) => Path; }
    private sealed class FakeRunner : ICliProcessRunner
    {
        public CliProcessResult Result { get; set; } = new(0, false);
        public TaskCompletionSource<CliProcessResult>? Wait { get; set; }
        public int CallCount { get; private set; }
        public Task<CliProcessResult> RunVisibleAsync(string executable, string arguments, TimeSpan timeout, CancellationToken cancellationToken)
        { CallCount++; return Wait?.Task ?? Task.FromResult(Result); }
        public Task<CliProcessResult> RunHiddenAsync(string executable, string arguments, TimeSpan timeout, CancellationToken cancellationToken)
        { CallCount++; return Wait?.Task ?? Task.FromResult(Result); }
    }
    private sealed class CancellingRunner : ICliProcessRunner
    {
        public Task<CliProcessResult> RunVisibleAsync(string executable, string arguments, TimeSpan timeout, CancellationToken cancellationToken)
            => Task.FromCanceled<CliProcessResult>(new CancellationToken(canceled: true));
        public Task<CliProcessResult> RunHiddenAsync(string executable, string arguments, TimeSpan timeout, CancellationToken cancellationToken)
            => Task.FromCanceled<CliProcessResult>(new CancellationToken(canceled: true));
    }
}
