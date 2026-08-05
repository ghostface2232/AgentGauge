using System.Diagnostics;
using CommunityToolkit.WinUI.Notifications;
using Gauge.Localization;
using Gauge.Models;

namespace Gauge.Services;

/// <summary>Raises evaluated usage alerts as Windows toast notifications.</summary>
public sealed class UsageNotificationService : IDisposable
{
    private readonly UsageNotificationEvaluator _evaluator = new();
    private NotificationPreferences _preferences = NotificationPreferences.Default;
    private bool _disposed;

    public UsageNotificationService()
    {
        // The Toolkit's compat layer auto-registers an AUMID + COM activator on the first
        // Show()/handler subscription for unpackaged apps, so it needs no Windows App SDK
        // Singleton package and keeps working under our self-contained xcopy deployment
        // (App SDK's own AppNotificationManager.Register() fails there — WindowsAppSDK #6071).
        // Subscribe activation up front so a click is handled in this running instance
        // instead of relaunching the app through the COM server. Windows owns queuing,
        // stacking, history, and Do Not Disturb / full-screen suppression.
        try
        {
            ToastNotificationManagerCompat.OnActivated += OnToastActivated;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Gauge] Toast activation subscribe failed: {ex.Message}");
        }
    }

    // Gauge's toasts are informational and carry no buttons or actions, so a click has
    // nothing to act on — but subscribing keeps Windows from launching a second instance
    // to deliver the activation.
    private void OnToastActivated(ToastNotificationActivatedEventArgsCompat e)
    {
    }

    /// <summary>
    /// Applies the notification preferences. With every kind off nothing can be raised, so
    /// <see cref="Process"/> is a no-op and detection stops; turning a kind back on resets
    /// the evaluator baseline, so re-enabling never replays crossings that happened while
    /// the app was silent. While at least one kind is on, detection keeps running for all
    /// kinds and the disabled ones are filtered at presentation — so toggling one kind off
    /// and on again never replays its stale alerts either.
    /// </summary>
    public void SetPreferences(NotificationPreferences preferences)
    {
        var wasEnabled = _preferences.Enabled;
        _preferences = preferences;
        if (preferences.Enabled && !wasEnabled)
        {
            _evaluator.ResetBaseline();
        }
    }

    public void Process(UsageState state)
    {
        if (!_preferences.Enabled) return;
        foreach (var notification in _evaluator.Evaluate(state, DateTimeOffset.Now))
        {
            if (_preferences.Allows(notification.Kind))
            {
                Show(notification);
            }
        }
    }

    /// <summary>Developer visual QA: one toast of every alert kind.</summary>
    public void ShowDemoSequence()
    {
        var now = DateTimeOffset.Now;
        var samples = new[]
        {
            DemoThreshold(UsageWindowType.FiveHour, UsageLevel.Danger, "Claude Code", 90, now.AddHours(2).AddMinutes(40), now),
            DemoThreshold(UsageWindowType.Weekly, UsageLevel.Caution, "Codex", 70, now.AddDays(4), now),
            DemoThreshold(UsageWindowType.Weekly, UsageLevel.Danger, "Codex", 90, now.AddDays(1), now),
            DemoReset(UsageWindowType.FiveHour, "Claude Code", 100, now),
            DemoReset(UsageWindowType.Weekly, "Codex", 100, now),
        };

        foreach (var sample in samples)
        {
            Show(sample);
        }
    }

    // Text-only: the toast already carries Gauge's app icon in the attribution row (from
    // the exe), and an app-logo override of the gauge image read cropped and clashed with
    // it, so the body stays just the title + message.
    private void Show(UsageNotification notification)
    {
        try
        {
            new ToastContentBuilder()
                .AddText(notification.Title)
                .AddText(notification.Message)
                .Show();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Gauge] Toast Show failed: {ex.Message}");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            ToastNotificationManagerCompat.OnActivated -= OnToastActivated;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Gauge] Toast activation unsubscribe failed: {ex.Message}");
        }
    }

    private static UsageNotification DemoThreshold(
        UsageWindowType windowType, UsageLevel level, string toolName, int percent,
        DateTimeOffset reset, DateTimeOffset now) => new()
    {
        Kind = UsageNotificationKind.Threshold,
        Level = level,
        ToolName = toolName,
        WindowType = windowType,
        Title = NotificationText.ThresholdTitle(toolName, DemoWindow(windowType), percent),
        Message = ResetTimeFormatter.ForNotification(reset, now),
        CreatedAt = now,
    };

    private static UsageNotification DemoReset(
        UsageWindowType windowType, string toolName, double availablePercent, DateTimeOffset now) => new()
    {
        Kind = UsageNotificationKind.Reset,
        Level = UsageLevel.Ok,
        ToolName = toolName,
        WindowType = windowType,
        Title = NotificationText.ResetTitle(toolName, DemoWindow(windowType)),
        Message = NotificationText.ResetMessage(availablePercent),
        CreatedAt = now,
    };

    private static UsageWindow DemoWindow(UsageWindowType type) => new()
    {
        Type = type,
        UsedRatio = 0,
        Label = WindowLabels.For(type),
    };
}
