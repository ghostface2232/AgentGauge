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
    private readonly Func<string, IAuthenticationProvider?> _accountProvider;

    /// <summary>Latest usage state, kept so plan labels can be (re)applied to cards as they
    /// are rebuilt — newly added cards pick up the plan without waiting for the next refresh.</summary>
    private UsageState _lastUsage = UsageState.Empty;

    public SettingsViewModel(
        ToolRegistry registry,
        IReadOnlyDictionary<ToolKind, IAuthenticationProvider> providers,
        UpdateService updateService,
        GlobalSettingsViewModel global,
        IAutoInstallAttempts? autoInstallAttempts = null,
        Func<string, IAuthenticationProvider?>? accountProvider = null)
    {
        _registry = registry;
        _providers = providers;
        _accountProvider = accountProvider ?? (_ => null);
        Global = global;
        Authentication = new ObservableCollection<AuthenticationCardViewModel>();
        Update = new UpdateViewModel(updateService, autoInstallAttempts);
        // Rebuild on add/remove, and re-sort on reorder (the latter so a drag on the main
        // screen reorders the cards here too, with no provider re-fetch).
        _registry.Changed += (_, _) => RebuildCards();
        _registry.OrderChanged += (_, _) => RebuildCards();
        _registry.AccountsChanged += (_, _) => RebuildCards();
        RebuildCards();
    }

    /// <summary>App-wide toggles (notifications, run-on-startup) shown above the service list.</summary>
    public GlobalSettingsViewModel Global { get; }
    public ObservableCollection<AuthenticationCardViewModel> Authentication { get; }
    public UpdateViewModel Update { get; }
    public event EventHandler<string>? AuthenticationSucceeded;
    public event EventHandler<bool>? SettingsWriteCompleted;

    /// <summary>Catalog tools not yet registered — the choices shown by the "+" picker.</summary>
    public IReadOnlyList<AddableTool> AddableTools =>
        _registry.Available.Select(kind => new AddableTool(kind, ToolCatalog.For(kind).DisplayName)).ToList();

    /// <summary>Registers a tool (from the "+" picker) and shows its card.</summary>
    public void AddTool(ToolKind kind) => _registry.Add(kind);

    public bool CanAddAccount(ToolKind kind, string label) => _registry.CanAddAccount(kind, label.Trim());

    public bool AddAccount(ToolKind kind, string label)
    {
        var saved = _registry.AddAccount(kind, label.Trim());
        SettingsWriteCompleted?.Invoke(this, saved);
        return saved;
    }

    /// <summary>
    /// Moves the dragged service on both screens through the shared registry order.
    /// </summary>
    public void MoveTool(int from, int to) =>
        _registry.MoveVisibleTool(Authentication.Select(card => (ToolKind?)card.Tool), from, to);

    public Task RefreshAsync() => Task.WhenAll(Authentication.Select(card => card.RefreshAsync()));

    /// <summary>
    /// Feeds the latest usage snapshots to the auth cards so each shows its plan label
    /// beside the signed-in status — the same plan the main screen displays. Called by the
    /// app whenever the coordinator pushes fresh usage.
    /// </summary>
    public void ApplyUsage(UsageState state)
    {
        _lastUsage = state;
        foreach (var card in Authentication)
        {
            card.ApplyPlan(PlanFor(card.Key));
        }
    }

    private string? PlanFor(string toolName) =>
        _lastUsage.Tools.FirstOrDefault(t => t.ToolName == toolName)?.Snapshot?.Plan;

    private void RemoveTool(ToolKind kind) => _registry.Remove(kind);

    /// <summary>
    /// Reconciles the card list with the registry: drops cards for tools no longer
    /// registered, appends cards for newly registered ones, reorders the existing cards to
    /// match the registry's display order, and kicks off a refresh for any new card.
    /// </summary>
    private void RebuildCards()
    {
        var targets = _registry.Enabled.SelectMany(tool => _registry.AccountKeysFor(tool)
            .Select(key => (Tool: tool, Key: key))).ToList();

        for (var i = Authentication.Count - 1; i >= 0; i--)
        {
            var card = Authentication[i];
            if (!targets.Any(target => target.Key == card.Key))
            {
                card.Detach();
                Authentication.RemoveAt(i);
            }
        }

        var index = 0;
        foreach (var (kind, key) in targets)
        {
            var existing = Authentication.FirstOrDefault(card => card.Key == key);
            if (existing is not null)
            {
                existing.ShowsNamedCopilotHint = existing.IsDefaultAccount
                    && kind == ToolKind.GitHubCopilot && _registry.TracksNamedCopilotAccounts;
                var current = Authentication.IndexOf(existing);
                if (current != index) Authentication.Move(current, index);
                index++;
                continue;
            }

            var account = _registry.FindAccount(key);
            var provider = account is null ? _providers.GetValueOrDefault(kind) : _accountProvider(key);
            if (provider is null) continue;

            var card = new AuthenticationCardViewModel(provider, account)
            {
                IsHidden = _registry.IsHidden(kind),
                ShowsNamedCopilotHint = account is null && kind == ToolKind.GitHubCopilot && _registry.TracksNamedCopilotAccounts,
            };
            card.HiddenChanged += (_, hidden) =>
            {
                var saved = _registry.SetHidden(kind, hidden);
                card.SyncHidden(_registry.IsHidden(kind));
                SettingsWriteCompleted?.Invoke(this, saved);
            };
            card.AuthenticationSucceeded += (_, _) =>
            {
                if (_registry.IsRegistered(key)) AuthenticationSucceeded?.Invoke(this, key);
            };
            card.RemoveRequested += (_, _) =>
            {
                if (account is null) RemoveTool(kind);
                else
                {
                    var saved = _registry.RemoveAccount(key);
                    SettingsWriteCompleted?.Invoke(this, saved);
                }
            };
            Authentication.Insert(index++, card);
            card.ApplyPlan(PlanFor(key));
            _ = card.RefreshAsync();
        }
    }
}
