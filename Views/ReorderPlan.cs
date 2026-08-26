namespace Gauge.Views;

/// <summary>
/// Pure index math for the press-and-drag card reorder, extracted from the gesture (which
/// needs a live visual tree) so the part that indexes the geometry snapshot is unit-testable.
///
/// The gesture snapshots every slot's laid-out position when the drag begins and then indexes
/// that snapshot while the pointer moves. Every index it derives must therefore stay inside
/// the snapshot, whatever the live list does meanwhile — a refresh landing mid-drag used to
/// let a live count reach past the snapshot arrays and crash the app.
/// </summary>
public static class ReorderPlan
{
    /// <summary>
    /// Maps slot → original index for the layout the list shows while the dragged card is
    /// held over <paramref name="target"/>: every other card keeps its relative order and
    /// steps aside to open the gap the dragged card would drop into. The result always has
    /// exactly <paramref name="count"/> entries, each a valid index, so a caller can index
    /// a snapshot of that length with either the slot or the value.
    ///
    /// This is also the arrangement the drop commits, so it must stay identical to what
    /// <c>ObservableCollection.Move(from, target)</c> produces — otherwise the cards would
    /// animate into one order and land in another.
    /// </summary>
    public static int[] SlotOrder(int count, int from, int target)
    {
        if (count <= 0)
        {
            return Array.Empty<int>();
        }

        from = Math.Clamp(from, 0, count - 1);
        var order = new List<int>(count);
        for (var i = 0; i < count; i++)
        {
            if (i != from)
            {
                order.Add(i);
            }
        }
        order.Insert(Math.Clamp(target, 0, order.Count), from);
        return order.ToArray();
    }

    /// <summary>
    /// Whether a drag that snapshotted <paramref name="snapshotCount"/> slots may still act on
    /// the list: the snapshot describes where the cards were laid out, so a list that has since
    /// gained or lost a card no longer matches it. Opening the popover forces a refresh, so a
    /// provider result CAN land mid-drag and add or remove a card — the gesture must abandon
    /// itself rather than move whatever now sits at the index it captured.
    /// </summary>
    public static bool SnapshotMatches(int liveCount, int snapshotCount)
        => snapshotCount > 0 && liveCount == snapshotCount;

    /// <summary>
    /// Whether a dropped reorder's two slots both address a collection of this size. The
    /// gesture measures the list it draws, which is not always the collection it moves — the
    /// usage list counts the panel's realized children — so the slots are checked once more
    /// against the collection itself, where an out-of-range index would throw out of
    /// <c>Move</c> rather than simply mis-ordering.
    /// </summary>
    public static bool CanCommit(int from, int to, int count)
        => from >= 0 && from < count && to >= 0 && to < count;
}
