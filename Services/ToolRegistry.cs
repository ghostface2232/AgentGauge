using System.Collections.ObjectModel;
using Gauge.Models;

namespace Gauge.Services;

/// <summary>
/// The ordered set of tools the user has registered. Registration is explicit (the
/// settings "+" button), not automatic on credential detection. Backed by an
/// <see cref="IToolRegistryStore"/> for persistence; raises <see cref="Changed"/> so
/// the usage pipeline and UI react to add/remove/reorder. <see cref="Enabled"/> follows
/// the user's saved display order (drag-to-reorder on either screen); <see cref="Available"/>
/// follows <see cref="ToolCatalog.All"/> declaration order. The display order is the single
/// source of truth both the main and settings screens read, so reordering on one screen
/// shows on the other.
/// </summary>
public sealed class ToolRegistry
{
    private readonly IToolRegistryStore _store;
    // Ordered: index is the tool's position in the UI. A list (not a set) so the user's
    // drag-to-reorder is preserved and persisted. N is tiny (the catalog), so Contains/IndexOf
    // scans are fine. Mutations happen only on the UI thread, but the coordinator's refresh
    // loop reads IsEnabled/Enabled from a thread-pool thread — so the collection is
    // copy-on-write: every mutation swaps in a freshly built list (volatile reference), and
    // a published snapshot is never mutated again. The read-only wrapper type makes that
    // never-mutate contract structural rather than comment-enforced. Readers therefore
    // always see a consistent snapshot, never a torn mid-resize state.
    private volatile ReadOnlyCollection<ToolKind> _enabled;

    public ToolRegistry(IToolRegistryStore store)
    {
        _store = store;
        _enabled = store.Load().Distinct().ToList().AsReadOnly();
    }

    /// <summary>Raised after the registered SET changes (add/remove), post-persist. The usage
    /// pipeline re-fetches on this; reordering does NOT raise it (see <see cref="OrderChanged"/>)
    /// so a drag never triggers a costly provider round-trip.</summary>
    public event EventHandler? Changed;

    /// <summary>Raised after only the display ORDER changes (drag-to-reorder), post-persist.
    /// The screens re-sort their cards in place; no re-fetch.</summary>
    public event EventHandler? OrderChanged;

    public bool IsEnabled(ToolKind kind) => _enabled.Contains(kind);

    /// <summary>Registered tools, in the user's saved display order. The returned snapshot
    /// is immutable (see <see cref="_enabled"/>), so handing it out directly is safe and
    /// allocation-free.</summary>
    public IReadOnlyList<ToolKind> Enabled => _enabled;

    /// <summary>Catalog tools not yet registered — the candidates for the "+" picker.</summary>
    public IReadOnlyList<ToolKind> Available
    {
        get
        {
            // Read the volatile field once so the whole filter runs against a single
            // snapshot; per-element reads could straddle a concurrent swap.
            var snapshot = _enabled;
            return ToolCatalog.All.Select(descriptor => descriptor.Kind)
                .Where(kind => !snapshot.Contains(kind)).ToList();
        }
    }

    public bool Add(ToolKind kind)
    {
        var snapshot = _enabled;
        if (snapshot.Contains(kind))
        {
            return false;
        }
        _enabled = new List<ToolKind>(snapshot) { kind }.AsReadOnly();
        Persist(membershipChanged: true);
        return true;
    }

    public bool Remove(ToolKind kind)
    {
        var snapshot = _enabled;
        var index = snapshot.IndexOf(kind);
        if (index < 0)
        {
            return false;
        }
        var next = new List<ToolKind>(snapshot);
        next.RemoveAt(index);
        _enabled = next.AsReadOnly();
        Persist(membershipChanged: true);
        return true;
    }

    /// <summary>
    /// Reorders the registered tools so the ones in <paramref name="visibleNewOrder"/> take
    /// that relative order. Only the slots those tools currently occupy are reassigned, so any
    /// enabled tool NOT present in <paramref name="visibleNewOrder"/> (e.g. the main screen
    /// reorders only the tools that have usage data, a subset) stays pinned in place. No-op
    /// (no persist, no <see cref="Changed"/>) when the resulting order is unchanged. Returns
    /// whether the order actually changed.
    /// </summary>
    public bool ReorderEnabled(IReadOnlyList<ToolKind> visibleNewOrder)
    {
        // Detect the change read-only against one snapshot first; the copy is built only
        // on the hit path, so a drag that ends where it started allocates nothing.
        var snapshot = _enabled;
        var newOrder = visibleNewOrder.Where(snapshot.Contains).Distinct().ToList();
        var slots = new List<int>();
        for (var i = 0; i < snapshot.Count; i++)
        {
            if (newOrder.Contains(snapshot[i]))
            {
                slots.Add(i);
            }
        }
        if (slots.Count != newOrder.Count)
        {
            return false;
        }

        var changed = false;
        for (var k = 0; k < slots.Count; k++)
        {
            if (snapshot[slots[k]] != newOrder[k])
            {
                changed = true;
                break;
            }
        }
        if (!changed)
        {
            return false;
        }

        var next = new List<ToolKind>(snapshot);
        for (var k = 0; k < slots.Count; k++)
        {
            next[slots[k]] = newOrder[k];
        }
        _enabled = next.AsReadOnly();
        Persist(membershipChanged: false);
        return true;
    }

    private void Persist(bool membershipChanged)
    {
        _store.Save(_enabled);
        if (membershipChanged)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
        else
        {
            OrderChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
