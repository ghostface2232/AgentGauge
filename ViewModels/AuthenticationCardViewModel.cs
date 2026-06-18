using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Gauge.Models;
using Gauge.Services;

namespace Gauge.ViewModels;

public sealed partial class AuthenticationCardViewModel : ObservableObject
{
    private readonly IAuthenticationProvider _provider;

    public AuthenticationCardViewModel(IAuthenticationProvider provider)
    {
        _provider = provider;
        ToolName = provider.State.ToolName;
        LoginCommand = new AsyncRelayCommand(LoginAsync, () => !IsLoginRunning);
        Apply(provider.State);
        provider.StateChanged += (_, state) => Apply(state);
    }

    public string ToolName { get; }
    public IAsyncRelayCommand LoginCommand { get; }
    public event EventHandler? AuthenticationSucceeded;

    [ObservableProperty] public partial string StatusText { get; set; } = "";
    [ObservableProperty] public partial string LoginButtonText { get; set; } = "로그인";
    [ObservableProperty] public partial bool IsLoginRunning { get; set; }

    private async Task LoginAsync()
    {
        var state = await _provider.LoginAsync();
        if (state.Status == AuthenticationStatus.Available)
        {
            AuthenticationSucceeded?.Invoke(this, EventArgs.Empty);
        }
    }

    public async Task RefreshAsync() => Apply(await _provider.RefreshStateAsync());

    private void Apply(AuthenticationState state)
    {
        StatusText = state.Message;
        IsLoginRunning = state.IsLoginRunning;
        LoginButtonText = state.Status switch
        {
            AuthenticationStatus.LoginRunning => "로그인 진행 중…",
            AuthenticationStatus.Available or AuthenticationStatus.Invalid => "다시 로그인 / 계정 전환",
            _ => "로그인",
        };
        LoginCommand.NotifyCanExecuteChanged();
    }
}
