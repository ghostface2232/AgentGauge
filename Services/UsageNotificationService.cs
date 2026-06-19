using System.Runtime.InteropServices;
using Gauge.Models;
using Gauge.Views;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace Gauge.Services;

/// <summary>Routes evaluated alerts through Windows' user-notification state.</summary>
public sealed class UsageNotificationService : IDisposable
{
    private readonly UsageNotificationEvaluator _evaluator = new();
    private readonly NotificationWindow _window = new();
    private readonly Queue<QueuedNotification> _displayQueue = new();
    private readonly DispatcherQueueTimer _suppressionTimer;
    private UsageNotification? _latestSuppressed;
    private int _suppressedCount;
    private bool _isShowing;
    private bool _disposed;

    public UsageNotificationService(DispatcherQueue dispatcher)
    {
        _window.Dismissed += OnDismissed;
        _suppressionTimer = dispatcher.CreateTimer();
        _suppressionTimer.Interval = TimeSpan.FromSeconds(2);
        _suppressionTimer.IsRepeating = true;
        _suppressionTimer.Tick += OnSuppressionTimerTick;
    }

    public void Process(UsageState state)
    {
        foreach (var notification in _evaluator.Evaluate(state, DateTimeOffset.Now))
        {
            if (CanPresentNow())
            {
                FlushSuppressedToDisplayQueue();
                _displayQueue.Enqueue(new QueuedNotification(notification, 0, null, null, false));
            }
            else
            {
                Suppress(notification);
            }
        }
        ShowNext();
    }

    /// <summary>Developer visual QA: every alert kind in light and dark, 3s each.</summary>
    public void ShowDemoSequence()
    {
        var now = DateTimeOffset.Now;
        var samples = new[]
        {
            Demo(UsageNotificationKind.Threshold, UsageLevel.Danger, UsageWindowType.FiveHour,
                "Claude Code · 5시간 한도 90% 도달", "2시간 40분 후 초기화", now),
            Demo(UsageNotificationKind.Threshold, UsageLevel.Caution, UsageWindowType.Weekly,
                "Codex · 주간 한도 70% 도달", "4일 후 (6월 23일) 초기화", now),
            Demo(UsageNotificationKind.Threshold, UsageLevel.Danger, UsageWindowType.Weekly,
                "Codex · 주간 한도 90% 도달", "1일 후 (6월 20일) 초기화", now),
            Demo(UsageNotificationKind.Reset, UsageLevel.Ok, UsageWindowType.FiveHour,
                "Claude Code · 5시간 한도 초기화", "현재 100%로 한도 초기화됨", now),
            Demo(UsageNotificationKind.Reset, UsageLevel.Ok, UsageWindowType.Weekly,
                "Codex · 주간 한도 초기화", "현재 100%로 한도 초기화됨", now),
        };

        foreach (var theme in new[] { ElementTheme.Light, ElementTheme.Dark })
        {
            foreach (var sample in samples)
            {
                _displayQueue.Enqueue(new QueuedNotification(
                    sample, 0, theme, TimeSpan.FromSeconds(3), true));
            }
        }
        ShowNext();
    }

    private void ShowNext()
    {
        if (_isShowing || _displayQueue.Count == 0) return;
        if (!_displayQueue.Peek().ForcePresent && !CanPresentNow())
        {
            while (_displayQueue.TryDequeue(out var queued))
            {
                Suppress(queued.Notification, Math.Max(1, queued.SuppressedCount));
            }
            return;
        }

        var next = _displayQueue.Dequeue();
        _isShowing = true;
        _window.Show(next.Notification, next.SuppressedCount, next.ThemeOverride, next.VisibleDuration);
    }

    private void OnDismissed(object? sender, EventArgs e)
    {
        _isShowing = false;
        ShowNext();
    }

    private void Suppress(UsageNotification notification, int count = 1)
    {
        _latestSuppressed = notification;
        _suppressedCount += count;
        if (!_suppressionTimer.IsRunning) _suppressionTimer.Start();
    }

    private void OnSuppressionTimerTick(DispatcherQueueTimer sender, object args)
    {
        if (!CanPresentNow()) return;
        FlushSuppressedToDisplayQueue();
        ShowNext();
    }

    private void FlushSuppressedToDisplayQueue()
    {
        if (_latestSuppressed is null) return;
        _displayQueue.Enqueue(new QueuedNotification(_latestSuppressed, _suppressedCount, null, null, false));
        _latestSuppressed = null;
        _suppressedCount = 0;
        _suppressionTimer.Stop();
    }

    private static bool CanPresentNow()
    {
        var result = NativeMethods.SHQueryUserNotificationState(out var state);
        // A query failure should not permanently silence Gauge. ACCEPTS_NOTIFICATIONS
        // is the only affirmative state; all fullscreen, presentation, busy, app and
        // quiet-time states are treated as suppression.
        return result < 0 || state == QueryUserNotificationState.AcceptsNotifications;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _suppressionTimer.Stop();
        _window.Dismissed -= OnDismissed;
        _window.Dispose();
    }

    private static UsageNotification Demo(
        UsageNotificationKind kind,
        UsageLevel level,
        UsageWindowType windowType,
        string title,
        string message,
        DateTimeOffset now) => new()
    {
        Kind = kind,
        Level = level,
        ToolName = title.Split('·')[0].Trim(),
        WindowType = windowType,
        Title = title,
        Message = message,
        CreatedAt = now,
    };

    private sealed record QueuedNotification(
        UsageNotification Notification,
        int SuppressedCount,
        ElementTheme? ThemeOverride,
        TimeSpan? VisibleDuration,
        bool ForcePresent);

    private enum QueryUserNotificationState
    {
        NotPresent = 1,
        Busy = 2,
        RunningDirect3DFullScreen = 3,
        PresentationMode = 4,
        AcceptsNotifications = 5,
        QuietTime = 6,
        App = 7,
    }

    private static class NativeMethods
    {
        [DllImport("shell32.dll")]
        public static extern int SHQueryUserNotificationState(out QueryUserNotificationState state);
    }
}
