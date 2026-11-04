namespace Gauge.Services;

/// <summary>
/// Subscribes to <see cref="Microsoft.Win32.SystemEvents"/> away from the UI thread's context.
///
/// SystemEvents captures the subscriber's SynchronizationContext and delivers through its
/// <c>Send</c>. On the UI thread that is DispatcherQueueSynchronizationContext, whose
/// <c>Send</c> always throws NotSupportedException, and SystemEvents swallows the exception
/// and drops the handler: no crash, no log, and the event is never heard again. Subscribing
/// here makes handlers run on SystemEvents' own window thread, so they must be thread-safe.
/// </summary>
public static class SystemEventsSubscription
{
    /// <summary>
    /// Runs <paramref name="subscribe"/> on a short-lived thread with no context, so nothing
    /// is left behind on a pool thread when SystemEvents installs a default context there.
    /// </summary>
    public static void RunWithoutSynchronizationContext(Action subscribe)
    {
        var thread = new Thread(() =>
        {
            try
            {
                subscribe();
            }
            catch (Exception ex)
            {
                // Unhandled on this thread it would end the process over a lost wake refresh.
                DiagnosticsLog.Write("app", $"System event subscription failed: {ex.GetType().Name}: {ex.Message}");
            }
        })
        {
            IsBackground = true,
            Name = "Gauge system event subscription",
        };
        thread.Start();
    }
}
