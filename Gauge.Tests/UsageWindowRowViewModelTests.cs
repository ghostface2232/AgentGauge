using Gauge.Models;
using Gauge.ViewModels;

namespace Gauge.Tests;

/// <summary>The absolute-counts caption: shown only when the provider reported both numbers.</summary>
public sealed class UsageWindowRowViewModelTests
{
    [Fact]
    public void CountsCaptionFormatsBothNumbers()
    {
        var row = new UsageWindowRowViewModel(Window() with { UsedTokens = 1280, LimitTokens = 3000 });

        Assert.Equal("1,280 / 3,000", row.CountsText);
        Assert.True(row.HasCounts);
    }

    [Fact]
    public void CountsCaptionHiddenWithoutBothNumbers()
    {
        Assert.False(new UsageWindowRowViewModel(Window()).HasCounts);
        Assert.False(new UsageWindowRowViewModel(Window() with { UsedTokens = 10 }).HasCounts);
        Assert.False(new UsageWindowRowViewModel(Window() with { LimitTokens = 300 }).HasCounts);
    }

    private static UsageWindow Window() => new()
    {
        Type = UsageWindowType.BillingCycle,
        Label = "월간",
        UsedRatio = 0.42,
    };
}
