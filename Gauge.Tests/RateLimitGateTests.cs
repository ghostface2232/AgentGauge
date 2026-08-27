using System.Net;
using Gauge.Providers;
using Gauge.Services;

namespace Gauge.Tests;

/// <summary>
/// The shared rate-limit cooldown: the retryable-status set, Retry-After beating the
/// backoff schedule (with the bogus-header clamp), extend-only blocking, the
/// user-initiated bypass, and the reset on success.
/// </summary>
public sealed class RateLimitGateTests
{
    [Theory]
    [InlineData(HttpStatusCode.RequestTimeout, true)]
    [InlineData(HttpStatusCode.TooManyRequests, true)]
    [InlineData(HttpStatusCode.InternalServerError, true)]
    [InlineData(HttpStatusCode.BadGateway, true)]
    [InlineData(HttpStatusCode.ServiceUnavailable, true)]
    [InlineData(HttpStatusCode.GatewayTimeout, true)]
    [InlineData(HttpStatusCode.Unauthorized, false)]
    [InlineData(HttpStatusCode.Forbidden, false)]
    [InlineData(HttpStatusCode.NotFound, false)]
    [InlineData(HttpStatusCode.OK, false)]
    public void RetryableStatusSet(HttpStatusCode status, bool retryable)
    {
        Assert.Equal(retryable, RateLimitGate.IsRetryable(status));
    }

    [Fact]
    public void NoStatusIsNotRetryable()
    {
        Assert.False(RateLimitGate.IsRetryable(null));
    }

    [Fact]
    public void FallbackScheduleEscalatesPerConsecutiveFailure()
    {
        var (gate, _) = Gate();

        Assert.Equal(TimeSpan.FromMinutes(2), gate.RecordFailure(null));
        Assert.Equal(TimeSpan.FromMinutes(4), gate.RecordFailure(null));
        Assert.Equal(TimeSpan.FromMinutes(8), gate.RecordFailure(null));
    }

    [Fact]
    public void RetryAfterOverridesTheSchedule()
    {
        var (gate, time) = Gate();

        gate.RecordFailure(TimeSpan.FromSeconds(90));

        time.Advance(TimeSpan.FromSeconds(89));
        Assert.True(gate.InCooldown(time.GetTimestamp()));
        time.Advance(TimeSpan.FromSeconds(2));
        Assert.False(gate.InCooldown(time.GetTimestamp()));
    }

    [Fact]
    public void BogusRetryAfterIsClampedToAnHour()
    {
        var (gate, _) = Gate();
        Assert.Equal(TimeSpan.FromHours(1), gate.RecordFailure(TimeSpan.FromDays(2)));
    }

    [Fact]
    public void NonPositiveRetryAfterFallsBackToTheSchedule()
    {
        var (gate, _) = Gate();
        Assert.Equal(TimeSpan.FromMinutes(2), gate.RecordFailure(TimeSpan.Zero));
    }

    [Fact]
    public void ShorterRetryAfterNeverShortensAStandingBlock()
    {
        var (gate, time) = Gate();
        gate.RecordFailure(TimeSpan.FromMinutes(10));

        time.Advance(TimeSpan.FromMinutes(1));
        gate.RecordFailure(TimeSpan.FromSeconds(5));

        time.Advance(TimeSpan.FromMinutes(5));
        Assert.True(gate.InCooldown(time.GetTimestamp())); // still inside the original 10m
    }

    [Fact]
    public void UserInitiatedFetchIsNeverBlocked()
    {
        var (gate, time) = Gate();
        gate.RecordFailure(null);

        Assert.True(gate.ShouldBlock(FetchInteraction.Background, time.GetTimestamp()));
        Assert.False(gate.ShouldBlock(FetchInteraction.UserInitiated, time.GetTimestamp()));
    }

    [Fact]
    public void SuccessClearsTheBlockAndTheEscalation()
    {
        var (gate, time) = Gate();
        gate.RecordFailure(null);
        gate.RecordFailure(null);

        gate.RecordSuccess();

        Assert.False(gate.InCooldown(time.GetTimestamp()));
        Assert.Equal(TimeSpan.FromMinutes(2), gate.RecordFailure(null)); // escalation restarted
    }

    private static (RateLimitGate Gate, MutableTime Time) Gate()
    {
        var time = new MutableTime();
        return (new RateLimitGate(new BackoffPolicy(TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(30)), time), time);
    }

    private sealed class MutableTime : TimeProvider
    {
        private long _timestamp = TimeSpan.TicksPerHour;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _timestamp;
        public void Advance(TimeSpan elapsed) => _timestamp += elapsed.Ticks;
    }
}
