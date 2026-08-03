using Gauge.Services;

namespace Gauge.Tests;

public sealed class BackoffPolicyTests
{
    [Theory]
    [InlineData(1, 2)]
    [InlineData(2, 4)]
    [InlineData(3, 8)]
    [InlineData(4, 16)]
    [InlineData(5, 30)] // capped
    [InlineData(6, 30)]
    [InlineData(100, 30)] // huge counts must not overflow past the cap
    public void DoublesFromBaseUpToCap(int consecutiveFailures, int expectedMinutes)
    {
        var policy = new BackoffPolicy(TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(30));
        Assert.Equal(TimeSpan.FromMinutes(expectedMinutes), policy.ForAttempt(consecutiveFailures));
    }

    [Fact]
    public void ZeroOrNegativeAttemptClampsToBase()
    {
        var policy = new BackoffPolicy(TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(30));
        Assert.Equal(TimeSpan.FromMinutes(2), policy.ForAttempt(0));
        Assert.Equal(TimeSpan.FromMinutes(2), policy.ForAttempt(-3));
    }
}
