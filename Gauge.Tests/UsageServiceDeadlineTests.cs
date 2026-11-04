using Gauge.Models;
using Gauge.Providers;
using Gauge.Services;

namespace Gauge.Tests;

/// <summary>
/// A read that never finishes must not take the others down with it: every result waits on the
/// slowest provider, and the coordinator's single gate is held for as long as that takes.
/// </summary>
public sealed class UsageServiceDeadlineTests
{
    private static readonly TimeSpan ShortDeadline = TimeSpan.FromMilliseconds(100);
    // Bounds a regression to a failed assertion rather than a hung test run.
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task AProviderThatNeverAnswersFailsAtTheDeadlineAndTheOthersStillReport()
    {
        var service = new UsageService(
            [new Provider(ToolKind.Codex, (_, _) => Task.FromResult(Snapshot("Codex"))),
             new Provider(ToolKind.ClaudeCode, (_, _) => new TaskCompletionSource<UsageSnapshot>().Task)],
            readDeadline: ShortDeadline);

        var results = await service.GetSnapshotsAsync(service.GetEnabledProviders(), default)
            .WaitAsync(TestTimeout);

        Assert.True(results.Single(r => r.Tool == ToolKind.Codex).Succeeded);
        Assert.IsType<TimeoutException>(results.Single(r => r.Tool == ToolKind.ClaudeCode).Error);
    }

    [Fact]
    public async Task AProviderBlockingBeforeItsFirstAwaitCanNotHoldUpTheOthers()
    {
        // WMI and process starts block synchronously, before any await a timeout could attach to.
        using var release = new ManualResetEventSlim();
        try
        {
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var service = new UsageService(
                [new Provider(ToolKind.ClaudeCode, (_, _) => { release.Wait(); return Task.FromResult(Snapshot("Claude")); }),
                 new Provider(ToolKind.Codex, (_, _) => { started.TrySetResult(); return Task.FromResult(Snapshot("Codex")); })],
                readDeadline: ShortDeadline);

            var results = await service.GetSnapshotsAsync(service.GetEnabledProviders(), default)
                .WaitAsync(TestTimeout);

            Assert.True(started.Task.IsCompleted);
            Assert.True(results.Single(r => r.Tool == ToolKind.Codex).Succeeded);
            Assert.IsType<TimeoutException>(results.Single(r => r.Tool == ToolKind.ClaudeCode).Error);
        }
        finally
        {
            release.Set();
        }
    }

    [Fact]
    public async Task AnAbandonedReadHasItsTokenCancelled()
    {
        var observedCancel = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new UsageService(
            [new Provider(ToolKind.Codex, async (_, token) =>
            {
                try
                {
                    await Task.Delay(Timeout.Infinite, token);
                    return Snapshot("Codex");
                }
                catch (OperationCanceledException)
                {
                    observedCancel.TrySetResult();
                    throw;
                }
            })],
            readDeadline: ShortDeadline);

        await service.GetSnapshotsAsync(service.GetEnabledProviders(), default).WaitAsync(TestTimeout);

        await observedCancel.Task.WaitAsync(TestTimeout);
    }

    [Fact]
    public async Task ShutdownCancellationIsReportedAsCancellationNotATimeout()
    {
        using var shutdown = new CancellationTokenSource();
        var service = new UsageService(
            [new Provider(ToolKind.Codex, (_, _) => new TaskCompletionSource<UsageSnapshot>().Task)],
            readDeadline: TimeSpan.FromMinutes(5));

        var pending = service.GetSnapshotsAsync(service.GetEnabledProviders(), shutdown.Token);
        await shutdown.CancelAsync();
        var results = await pending.WaitAsync(TestTimeout);

        Assert.IsAssignableFrom<OperationCanceledException>(Assert.Single(results).Error);
    }

    private static UsageSnapshot Snapshot(string tool) => new()
    {
        ToolName = tool,
        CapturedAt = DateTimeOffset.UnixEpoch,
        Windows = [],
    };

    private sealed class Provider(
        ToolKind tool,
        Func<FetchInteraction, CancellationToken, Task<UsageSnapshot>> read) : IUsageProvider
    {
        public ToolKind Tool => tool;
        public string ToolName => ToolCatalog.For(tool).DisplayName;
        public Task<UsageSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
            => read(FetchInteraction.Background, cancellationToken);
        public Task<UsageSnapshot> GetSnapshotAsync(FetchInteraction interaction, CancellationToken cancellationToken)
            => read(interaction, cancellationToken);
    }
}
