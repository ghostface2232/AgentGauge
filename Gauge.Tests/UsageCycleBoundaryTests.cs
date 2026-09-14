using Gauge.ViewModels;

namespace Gauge.Tests;

/// <summary>
/// Where a recorded series crosses into a new quota cycle: a reset timestamp that moved on,
/// or — only once the timestamps cannot answer — a decrease too large to be the provider
/// recalculating its own utilization.
/// </summary>
public sealed class UsageCycleBoundaryTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 14, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Reset = Now.AddHours(2);

    // The magnitude of a decrease decides nothing while the reset instant stands still:
    // Cursor's headline percent comes from a precedence chain that can change shape between
    // polls, so even a collapse there is a recalculation and the row keeps its history.
    [Theory]
    [InlineData(.421, .419)]
    [InlineData(.421, .421)]
    [InlineData(.400, .430)]
    [InlineData(.500, .451)]
    [InlineData(.900, .050)]
    public void UnchangedResetTimestampIsOneCycleWhateverTheRatiosDo(double before, double after) =>
        Assert.False(UsageCycleBoundary.IsReset(
            new(Now.AddMinutes(-5), before, Reset), new(Now, after, Reset)));

    [Fact]
    public void ResetTimestampAdvanceWithAnyDecreaseIsABoundary()
    {
        Assert.True(UsageCycleBoundary.IsReset(
            new(Now.AddMinutes(-5), .421, Reset), new(Now, .419, Reset.AddHours(5))));
        // A reset instant recomputed by seconds is jitter, not a new cycle.
        Assert.False(UsageCycleBoundary.IsReset(
            new(Now.AddMinutes(-5), .421, Reset), new(Now, .419, Reset.AddSeconds(30))));
        // The advance alone proves nothing while usage keeps climbing.
        Assert.False(UsageCycleBoundary.IsReset(
            new(Now.AddMinutes(-5), .419, Reset), new(Now, .421, Reset.AddHours(5))));
    }

    [Fact]
    public void MissingResetTimestampsFallBackToTheSizeOfTheDrop()
    {
        Assert.True(UsageCycleBoundary.IsReset(new(Now.AddMinutes(-5), .90), new(Now, .05)));
        Assert.False(UsageCycleBoundary.IsReset(new(Now.AddMinutes(-5), .421), new(Now, .419)));
        // One side alone cannot establish an advance, so the drop decides there too.
        Assert.True(UsageCycleBoundary.IsReset(new(Now.AddMinutes(-5), .90, Reset), new(Now, .05)));
        Assert.False(UsageCycleBoundary.IsReset(new(Now.AddMinutes(-5), .421), new(Now, .419, Reset)));
    }
}
