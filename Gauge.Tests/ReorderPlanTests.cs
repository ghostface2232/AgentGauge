using Gauge.Views;

namespace Gauge.Tests;

/// <summary>
/// The drag-reorder index math extracted from the gesture: the shift layout while a card is
/// held over a slot, and the snapshot-validity rule that stops a refresh landing mid-drag
/// from indexing past the captured geometry (the crash this extraction was made to fix).
/// </summary>
public sealed class ReorderPlanTests
{
    [Theory]
    // Dragging the first card down to the last slot pulls every card below it up one.
    [InlineData(3, 0, 2, new[] { 1, 2, 0 })]
    // ...and the last card up to the first slot pushes every card above it down one.
    [InlineData(3, 2, 0, new[] { 2, 0, 1 })]
    // Held over its own slot, nothing moves.
    [InlineData(3, 1, 1, new[] { 0, 1, 2 })]
    [InlineData(4, 1, 2, new[] { 0, 2, 1, 3 })]
    [InlineData(2, 0, 1, new[] { 1, 0 })]
    public void PlansTheShiftLayoutForTheHeldSlot(int count, int from, int target, int[] expected)
        => Assert.Equal(expected, ReorderPlan.SlotOrder(count, from, target));

    [Theory]
    [InlineData(5, 0, 0)]
    [InlineData(5, 4, 4)]
    [InlineData(5, 2, 0)]
    [InlineData(5, 2, 4)]
    [InlineData(1, 0, 0)]
    public void EverySlotAndValueIsAValidIndex(int count, int from, int target)
    {
        var order = ReorderPlan.SlotOrder(count, from, target);

        // The gesture indexes its geometry snapshot by both the slot and the value, so the
        // plan must be a permutation of exactly the snapshot's indices — nothing beyond it.
        Assert.Equal(count, order.Length);
        Assert.Equal(Enumerable.Range(0, count), order.OrderBy(i => i));
    }

    [Theory]
    // Indices from before a card was removed, or from a stale target, must not escape the plan.
    [InlineData(3, 9, 1)]
    [InlineData(3, -1, 1)]
    [InlineData(3, 1, 9)]
    [InlineData(3, 1, -4)]
    public void OutOfRangeInputsAreClampedRatherThanOverrunning(int count, int from, int target)
    {
        var order = ReorderPlan.SlotOrder(count, from, target);
        Assert.Equal(count, order.Length);
        Assert.Equal(Enumerable.Range(0, count), order.OrderBy(i => i));
    }

    [Fact]
    public void AnEmptyListPlansNothing()
    {
        Assert.Empty(ReorderPlan.SlotOrder(0, 0, 0));
        Assert.Empty(ReorderPlan.SlotOrder(-1, 0, 0));
    }

    [Theory]
    [InlineData(2, 0, 1)]
    [InlineData(3, 0, 2)]
    [InlineData(4, 3, 0)]
    [InlineData(5, 1, 3)]
    [InlineData(5, 3, 1)]
    [InlineData(5, 2, 2)]
    public void ThePreviewedArrangementIsTheOneTheDropCommits(int count, int from, int target)
    {
        // The shift animation previews SlotOrder while the drop calls ObservableCollection
        // .Move, which is a remove-then-insert. If the two ever disagreed, cards would
        // animate into one order and land in another.
        var moved = Enumerable.Range(0, count).ToList();
        moved.RemoveAt(from);
        moved.Insert(target, from);

        Assert.Equal(moved, ReorderPlan.SlotOrder(count, from, target));
    }

    [Theory]
    [InlineData(0, 1, 3, true)]
    [InlineData(2, 0, 3, true)]
    // A slot the collection no longer has must never reach Move, which throws on it.
    [InlineData(3, 0, 3, false)]
    [InlineData(0, 3, 3, false)]
    [InlineData(-1, 0, 3, false)]
    [InlineData(0, -1, 3, false)]
    // Nothing is committable against an empty collection.
    [InlineData(0, 0, 0, false)]
    public void CommitOnlyAcceptsSlotsTheCollectionStillHas(int from, int to, int count, bool expected)
        => Assert.Equal(expected, ReorderPlan.CanCommit(from, to, count));

    [Theory]
    [InlineData(3, 3, true)]
    // A card added or removed mid-drag invalidates the captured layout in both directions.
    [InlineData(4, 3, false)]
    [InlineData(2, 3, false)]
    // Before a drag begins there is no snapshot to match, so nothing may act on one.
    [InlineData(0, 0, false)]
    [InlineData(3, 0, false)]
    public void SnapshotMatchesOnlyAnUnchangedList(int liveCount, int snapshotCount, bool expected)
        => Assert.Equal(expected, ReorderPlan.SnapshotMatches(liveCount, snapshotCount));
}
