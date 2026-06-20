using Gauge.Services;

namespace Gauge.Tests;

/// <summary>
/// The delegated refresher nudges the Claude CLI to refresh its own token, with a
/// cooldown so it never spawns the CLI on every poll.
/// </summary>
public sealed class ClaudeTokenRefresherTests
{
    [Fact]
    public async Task RunsCliThenHonorsCooldownUntilItElapses()
    {
        var now = 1_000L;
        var runner = new CountingRunner();
        var refresher = new ClaudeTokenRefresher(new FakeLocator("claude.exe"), runner, () => now);

        Assert.True(await refresher.TryRefreshAsync(default));
        Assert.Equal(1, runner.HiddenCalls);

        // A second attempt inside the cooldown window is skipped (no CLI run).
        Assert.False(await refresher.TryRefreshAsync(default));
        Assert.Equal(1, runner.HiddenCalls);

        // Past the 5-minute success cooldown it runs again.
        now += (long)TimeSpan.FromMinutes(6).TotalMilliseconds;
        Assert.True(await refresher.TryRefreshAsync(default));
        Assert.Equal(2, runner.HiddenCalls);
    }

    [Fact]
    public async Task MissingCliNeverRunsAndReportsSkip()
    {
        var runner = new CountingRunner();
        var refresher = new ClaudeTokenRefresher(new FakeLocator(null), runner, () => 1_000L);

        Assert.False(await refresher.TryRefreshAsync(default));
        Assert.Equal(0, runner.HiddenCalls);
    }

    [Fact]
    public async Task TimeoutUsesShorterRetryCooldown()
    {
        var now = 1_000L;
        var runner = new CountingRunner { Result = new CliProcessResult(-1, TimedOut: true) };
        var refresher = new ClaudeTokenRefresher(new FakeLocator("claude.exe"), runner, () => now);

        Assert.True(await refresher.TryRefreshAsync(default));

        // Still cooling down after 30s...
        now += (long)TimeSpan.FromSeconds(30).TotalMilliseconds;
        Assert.False(await refresher.TryRefreshAsync(default));
        Assert.Equal(1, runner.HiddenCalls);

        // ...but the retry cooldown is only 1 minute, not 5.
        now += (long)TimeSpan.FromSeconds(40).TotalMilliseconds;
        Assert.True(await refresher.TryRefreshAsync(default));
        Assert.Equal(2, runner.HiddenCalls);
    }

    private sealed class FakeLocator(string? path) : ICliLocator
    {
        public string? Find(string commandName) => path;
    }

    private sealed class CountingRunner : ICliProcessRunner
    {
        public int HiddenCalls { get; private set; }
        public CliProcessResult Result { get; set; } = new(0, false);

        public Task<CliProcessResult> RunVisibleAsync(string executable, string arguments, TimeSpan timeout, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<CliProcessResult> RunHiddenAsync(string executable, string arguments, TimeSpan timeout, CancellationToken cancellationToken)
        {
            HiddenCalls++;
            return Task.FromResult(Result);
        }
    }
}
