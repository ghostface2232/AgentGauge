using Gauge.Services;

namespace Gauge.Tests;

/// <summary>
/// SystemEvents delivers through the SynchronizationContext its subscriber had, and drops the
/// handler without a trace if that context's Send throws, as the UI thread's does.
/// </summary>
public sealed class SystemEventsSubscriptionTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task SubscribesWithoutTheCallersSynchronizationContext()
    {
        var previous = SynchronizationContext.Current;
        var uiLike = new SendThrowingContext();
        var seen = new TaskCompletionSource<(SynchronizationContext? Context, int ThreadId)>();
        try
        {
            SynchronizationContext.SetSynchronizationContext(uiLike);
            SystemEventsSubscription.RunWithoutSynchronizationContext(
                () => seen.SetResult((SynchronizationContext.Current, Environment.CurrentManagedThreadId)));
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }

        var (context, threadId) = await seen.Task.WaitAsync(TestTimeout);

        Assert.Null(context);
        Assert.NotEqual(Environment.CurrentManagedThreadId, threadId);
    }

    [Fact]
    public async Task AFailedSubscriptionDoesNotEscapeItsThread()
    {
        var ran = new TaskCompletionSource();
        SystemEventsSubscription.RunWithoutSynchronizationContext(() =>
        {
            ran.SetResult();
            throw new InvalidOperationException("no session");
        });

        // Escaping would end the test process rather than fail this line.
        await ran.Task.WaitAsync(TestTimeout);
    }

    private sealed class SendThrowingContext : SynchronizationContext
    {
        public override void Send(SendOrPostCallback d, object? state)
            => throw new NotSupportedException("The Send method is not supported, use Post instead.");
    }
}
