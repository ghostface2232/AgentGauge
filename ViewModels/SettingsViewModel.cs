using System.Collections.ObjectModel;
using Gauge.Services;

namespace Gauge.ViewModels;

public sealed class SettingsViewModel
{
    public SettingsViewModel(IEnumerable<IAuthenticationProvider> providers)
    {
        Authentication = new ObservableCollection<AuthenticationCardViewModel>(
            providers.Select(provider => new AuthenticationCardViewModel(provider)));
        foreach (var card in Authentication)
        {
            card.AuthenticationSucceeded += (_, _) => AuthenticationSucceeded?.Invoke(this, EventArgs.Empty);
        }
    }

    public ObservableCollection<AuthenticationCardViewModel> Authentication { get; }
    public event EventHandler? AuthenticationSucceeded;

    public Task RefreshAsync() => Task.WhenAll(Authentication.Select(card => card.RefreshAsync()));
}
