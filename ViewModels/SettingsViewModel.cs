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

    /// <summary>Latest usage state, kept so plan labels can be (re)applied to cards as they
    /// are rebuilt — newly added cards pick up the plan without waiting for the next refresh.</summary>
    private UsageState _lastUsage = UsageState.Empty;

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
        // Rebuild on add/remove, and re-sort on reorder (the latter so a drag on the main
        // screen reorders the cards here too, with no provider re-fetch).
        _registry.Changed += (_, _) => RebuildCards();
        _registry.OrderChanged += (_, _) => RebuildCards();
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
    public void AddTool(ToolKind kind) => _registry.Add(kind);

    /// <summary>
    /// Persists a new card order after a drag on the settings screen. The registry raises
    /// <see cref="ToolRegistry.Changed"/>, which both re-syncs these cards (a no-op since the
    /// dragged collection already matches) and propagates the order to the main screen.
    /// </summary>
    public void ReorderTools(IReadOnlyList<ToolKind> newOrder) => _registry.ReorderEnabled(newOrder);

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
            card.ApplyPlan(PlanFor(card.ToolName));
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
            card.ApplyPlan(PlanFor(card.ToolName));
            _ = card.RefreshAsync();
        }

        // Reorder cards in place to match the registry order (e.g. after a reorder elsewhere).
        for (var target = 0; target < enabled.Count; target++)
        {
            var kind = enabled[target];
            var current = -1;
            for (var i = target; i < Authentication.Count; i++)
            {
                if (Authentication[i].Tool == kind)
                {
                    current = i;
                    break;
                }
            }
            if (current > target)
            {
                Authentication.Move(current, target);
            }
        }
    }
}
