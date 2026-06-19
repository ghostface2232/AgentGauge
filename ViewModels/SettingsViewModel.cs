using System.Collections.ObjectModel;
using Gauge.Models;
using Gauge.Services;

namespace Gauge.ViewModels;

/// <summary>One catalog tool the user can add via the settings "+" picker.</summary>
public sealed record AddableTool(ToolKind Kind, string DisplayName);

/// <summary>
/// Settings screen state. The list of authentication cards mirrors the
/// <see cref="ToolRegistry"/> (registered tools only); the "+" picker offers the
/// remaining catalog tools, and each card can disconnect its tool. Auth providers are
/// supplied for the whole catalog so a card can be built the moment a tool is added.
/// </summary>
public sealed class SettingsViewModel
{
    private readonly ToolRegistry _registry;
    private readonly IReadOnlyDictionary<ToolKind, IAuthenticationProvider> _providers;

    public SettingsViewModel(
        ToolRegistry registry,
        IReadOnlyDictionary<ToolKind, IAuthenticationProvider> providers,
        UpdateService updateService,
        GlobalSettingsViewModel global)
    {
        _registry = registry;
        _providers = providers;
        Global = global;
        Authentication = new ObservableCollection<AuthenticationCardViewModel>();
        Update = new UpdateViewModel(updateService);
        RebuildCards();
    }

    /// <summary>App-wide toggles (notifications, run-on-startup) shown above the service list.</summary>
    public GlobalSettingsViewModel Global { get; }
    public ObservableCollection<AuthenticationCardViewModel> Authentication { get; }
    public UpdateViewModel Update { get; }
    public event EventHandler? AuthenticationSucceeded;

    /// <summary>Catalog tools not yet registered — the choices shown by the "+" picker.</summary>
    public IReadOnlyList<AddableTool> AddableTools =>
        _registry.Available.Select(kind => new AddableTool(kind, ToolCatalog.For(kind).DisplayName)).ToList();

    /// <summary>Registers a tool (from the "+" picker) and shows its card.</summary>
    public void AddTool(ToolKind kind)
    {
        if (_registry.Add(kind))
        {
            RebuildCards();
        }
    }

    public Task RefreshAsync() => Task.WhenAll(Authentication.Select(card => card.RefreshAsync()));

    private void RemoveTool(ToolKind kind)
    {
        if (_registry.Remove(kind))
        {
            RebuildCards();
        }
    }

    /// <summary>
    /// Reconciles the card list with the registry: drops cards for tools no longer
    /// registered, appends cards for newly registered ones (in catalog order), and
    /// kicks off a refresh for any new card.
    /// </summary>
    private void RebuildCards()
    {
        var enabled = _registry.Enabled;

        for (var i = Authentication.Count - 1; i >= 0; i--)
        {
            if (!enabled.Contains(Authentication[i].Tool))
            {
                Authentication.RemoveAt(i);
            }
        }

        foreach (var kind in enabled)
        {
            if (Authentication.Any(card => card.Tool == kind))
            {
                continue;
            }
            if (!_providers.TryGetValue(kind, out var provider))
            {
                continue;
            }

            var card = new AuthenticationCardViewModel(provider);
            card.AuthenticationSucceeded += (_, _) => AuthenticationSucceeded?.Invoke(this, EventArgs.Empty);
            card.RemoveRequested += (_, _) => RemoveTool(card.Tool);
            Authentication.Add(card);
            _ = card.RefreshAsync();
        }
    }
}
