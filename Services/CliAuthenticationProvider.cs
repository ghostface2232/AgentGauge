using Gauge.Models;
using System.Security.Cryptography;
using System.Text;

namespace Gauge.Services;

public sealed class CliAuthenticationProvider : IAuthenticationProvider
{
    private static readonly TimeSpan LoginTimeout = TimeSpan.FromMinutes(10);
    private readonly ICredentialSource _credentials;
    private readonly ICliLocator _locator;
    private readonly ICliProcessRunner _runner;
    private readonly string _command;
    private readonly string _arguments;
    private readonly SemaphoreSlim _loginGate = new(1, 1);
    private string? _lastCredentialFingerprint;
    private string? _rejectedCredentialFingerprint;
    private bool _credentialsRejected;

    public CliAuthenticationProvider(
        ToolKind tool, ICredentialSource credentials, ICliLocator locator, ICliProcessRunner runner)
    {
        Tool = tool;
        _credentials = credentials;
        _locator = locator;
        _runner = runner;
        (_command, _arguments) = tool switch
        {
            ToolKind.ClaudeCode => ("claude", "/login"),
            ToolKind.Codex => ("codex", "login"),
            _ => throw new ArgumentOutOfRangeException(nameof(tool)),
        };
        State = MissingState();
    }

    public ToolKind Tool { get; }
    public AuthenticationState State { get; private set; }
    public bool IsLoginRunning => State.Status == AuthenticationStatus.LoginRunning;
    public event EventHandler<AuthenticationState>? StateChanged;

    public async Task<AuthenticationState> RefreshStateAsync(CancellationToken cancellationToken = default)
    {
        if (IsLoginRunning) return State;
        var result = await _credentials.ReadAsync(Tool, cancellationToken);
        return SetState(FromCredential(result));
    }

    public async Task<AuthenticationState> LoginAsync(CancellationToken cancellationToken = default)
    {
        if (!await _loginGate.WaitAsync(0, cancellationToken)) return State;
        try
        {
            var executable = _locator.Find(_command);
            if (executable is null)
            {
                return SetState(Failed($"{_command} CLI를 찾을 수 없습니다. CLI를 설치한 뒤 `{_command} {_arguments}`를 실행하세요."));
            }

            SetState(NewState(AuthenticationStatus.LoginRunning, CredentialSource.None,
                "브라우저 또는 CLI 창에서 로그인을 완료하세요."));
            CliProcessResult result;
            try
            {
                result = await _runner.RunVisibleAsync(executable, _arguments, LoginTimeout, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return SetState(Failed("로그인이 취소되었습니다."));
            }
            catch (Exception)
            {
                return SetState(Failed("CLI 로그인 프로세스를 실행하지 못했습니다."));
            }

            if (result.TimedOut) return SetState(Failed("로그인 제한 시간(10분)이 지났습니다. 다시 시도하세요."));
            if (result.ExitCode != 0) return SetState(Failed($"CLI 로그인이 완료되지 않았습니다. 종료 코드: {result.ExitCode}"));

            var credential = await _credentials.ReadAsync(Tool, cancellationToken);
            if (credential.Status != CredentialReadStatus.Available)
            {
                return SetState(Failed("CLI가 정상 종료됐지만 로그인 정보를 찾지 못했습니다. CLI에서 로그인을 확인하세요."));
            }
            // A completed official CLI login gets one fresh API attempt even if the
            // CLI retained the same token. A subsequent 401/403 marks it invalid again.
            _credentialsRejected = false;
            _rejectedCredentialFingerprint = null;
            return SetState(FromCredential(credential));
        }
        finally
        {
            _loginGate.Release();
        }
    }

    public void ReportInvalidCredentials()
    {
        if (!IsLoginRunning)
        {
            _credentialsRejected = true;
            _rejectedCredentialFingerprint = _lastCredentialFingerprint;
            SetState(NewState(AuthenticationStatus.Invalid, _credentials.Source,
                "로그인이 만료되었거나 거부되었습니다. 다시 로그인하세요."));
        }
    }

    private AuthenticationState FromCredential(CredentialReadResult result)
    {
        if (result.Status == CredentialReadStatus.Available)
        {
            var credential = result.Credential!;
            var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(credential.AccessToken)));
            if (_credentialsRejected && StringComparer.Ordinal.Equals(_rejectedCredentialFingerprint, fingerprint))
            {
                _lastCredentialFingerprint = fingerprint;
                return NewState(AuthenticationStatus.Invalid, credential.Source,
                    "로그인이 만료되었거나 거부되었습니다. 다시 로그인하세요.");
            }

            _credentialsRejected = false;
            _rejectedCredentialFingerprint = null;
            _lastCredentialFingerprint = fingerprint;
            return NewState(AuthenticationStatus.Available, credential.Source,
                credential.Plan is { Length: > 0 } plan
                    ? $"로그인됨 · {plan}"
                    : "로그인됨");
        }

        _lastCredentialFingerprint = null;
        return result.Status == CredentialReadStatus.Invalid
            ? NewState(AuthenticationStatus.Invalid, _credentials.Source,
                result.Message ?? "로그인 정보가 올바르지 않습니다.")
            : MissingState();
    }

    private AuthenticationState MissingState() => NewState(AuthenticationStatus.Missing, CredentialSource.None,
        "더 정확한 사용량 정보를 보려면 로그인해주세요.");

    private AuthenticationState Failed(string message) => NewState(AuthenticationStatus.LoginFailed, CredentialSource.None, message);

    private AuthenticationState NewState(AuthenticationStatus status, CredentialSource source, string message) => new()
    {
        Tool = Tool,
        ToolName = Tool == ToolKind.ClaudeCode ? "Claude Code" : "Codex",
        Status = status,
        Source = source,
        Message = message,
    };

    private AuthenticationState SetState(AuthenticationState state)
    {
        State = state;
        StateChanged?.Invoke(this, state);
        return state;
    }
}
