using Gauge.Localization;
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
    private readonly ToolDescriptor _descriptor;
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
        _descriptor = ToolCatalog.For(tool);
        (_command, _arguments) = (_descriptor.LoginCommand, _descriptor.LoginArguments);
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
                return SetState(Failed(Loc.Format("Auth_CliNotFound", _command, $"{_command} {_arguments}")));
            }

            SetState(NewState(AuthenticationStatus.LoginRunning, CredentialSource.None,
                Loc.Get("Auth_LoginInBrowser")));
            CliProcessResult result;
            try
            {
                result = await _runner.RunVisibleAsync(executable, _arguments, LoginTimeout, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return SetState(Failed(Loc.Get("Auth_LoginCancelled")));
            }
            catch (Exception)
            {
                return SetState(Failed(Loc.Get("Auth_LoginProcessFailed")));
            }

            if (result.TimedOut) return SetState(Failed(Loc.Get("Auth_LoginTimeout")));
            if (result.ExitCode != 0) return SetState(Failed(Loc.Format("Auth_LoginBadExit", result.ExitCode)));

            var credential = await _credentials.ReadAsync(Tool, cancellationToken);
            if (credential.Status != CredentialReadStatus.Available)
            {
                return SetState(Failed(Loc.Get("Auth_CredentialNotFound")));
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
                Loc.Get("Auth_Expired")));
        }
    }

    public void ReportCredentialsAccepted()
    {
        if (IsLoginRunning || !_credentialsRejected)
        {
            return;
        }
        _credentialsRejected = false;
        _rejectedCredentialFingerprint = null;
        // Flip the card back to signed-in right away; the exact plan label is refreshed on
        // the next RefreshStateAsync (the credential file already has it).
        if (State.Status == AuthenticationStatus.Invalid)
        {
            SetState(NewState(AuthenticationStatus.Available, _credentials.Source, Loc.Get("Auth_SignedIn")));
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
                    Loc.Get("Auth_Expired"));
            }

            _credentialsRejected = false;
            _rejectedCredentialFingerprint = null;
            _lastCredentialFingerprint = fingerprint;
            return NewState(AuthenticationStatus.Available, credential.Source,
                credential.Plan is { Length: > 0 } plan
                    ? Loc.Format("Auth_SignedInWithPlan", plan)
                    : Loc.Get("Auth_SignedIn"));
        }

        _lastCredentialFingerprint = null;
        return result.Status == CredentialReadStatus.Invalid
            ? NewState(AuthenticationStatus.Invalid, _credentials.Source,
                result.Message ?? Loc.Get("Auth_InvalidCredential"))
            : MissingState();
    }

    private AuthenticationState MissingState() => NewState(AuthenticationStatus.Missing, CredentialSource.None,
        Loc.Get("Auth_Missing"));

    private AuthenticationState Failed(string message) => NewState(AuthenticationStatus.LoginFailed, CredentialSource.None, message);

    private AuthenticationState NewState(AuthenticationStatus status, CredentialSource source, string message) => new()
    {
        Tool = Tool,
        ToolName = _descriptor.DisplayName,
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
