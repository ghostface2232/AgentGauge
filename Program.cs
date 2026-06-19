using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

namespace Gauge;

/// <summary>
/// Custom entry point. Runs single-instance detection before starting the XAML
/// application so a second launch can exit silently without ever spinning up UI.
/// </summary>
public static class Program
{
    // Unique key for this app's single-instance registration.
    private const string AppKey = "Gauge.SingleInstance";

    [STAThread]
    private static int Main(string[] args)
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();

        if (IsSecondaryInstance(args))
        {
            // Tray-only background app: there is no window to surface, so the
            // second instance just exits quietly. Activation redirection can be
            // added later if we want the running instance to show the popover.
            return 0;
        }

        Application.Start(p =>
        {
            var context = new DispatcherQueueSynchronizationContext(
                DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            _ = new App();
        });

        return 0;
    }

    private static bool IsSecondaryInstance(string[] args)
    {
        // Visual notification QA may run beside the normal tray instance without
        // redirecting or disturbing it.
        var isNotificationDemo = args.Contains("--notification-demo", StringComparer.OrdinalIgnoreCase);
        var keyInstance = AppInstance.FindOrRegisterForKey(
            isNotificationDemo ? $"{AppKey}.NotificationDemo" : AppKey);
        return !keyInstance.IsCurrent;
    }
}
