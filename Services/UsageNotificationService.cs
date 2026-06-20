using System.Linq;
using System.Runtime.InteropServices;
using Gauge.Localization;
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
    private bool _isShowing;
    private bool _enabled = true;
    private bool _disposed;

    public UsageNotificationService(DispatcherQueue dispatcher)
    {
        _window.Dismissed += OnDismissed;
        _suppressionTimer = dispatcher.CreateTimer();
        _suppressionTimer.Interval = TimeSpan.FromSeconds(2);
        _suppressionTimer.IsRepeating = true;
        _suppressionTimer.Tick += OnSuppressionTimerTick;
    }

    /// <summary>
    /// Turns usage notifications on or off (the global settings toggle). While off,
    /// <see cref="Process"/> is a no-op and anything queued or held during Do Not Disturb
    /// is dropped, so flipping back on never replays alerts accumulated while silenced.
    /// </summary>
    public void SetEnabled(bool enabled)
    {
        var wasEnabled = _enabled;
        _enabled = enabled;
        if (enabled)
        {
            if (!wasEnabled)
            {
                _evaluator.ResetBaseline();
            }
            return;
        }

        _suppressionTimer.Stop();
        _displayQueue.Clear();
        _latestSuppressed = null;
    }

    public void Process(UsageState state)
    {
        if (!_enabled) return;

        foreach (var notification in _evaluator.Evaluate(state, DateTimeOffset.Now))
        {
            if (CanPresentNow())
            {
                FlushSuppressedToDisplayQueue();
                _displayQueue.Enqueue(new QueuedNotification(notification, null, null, false));
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
            DemoThreshold(UsageWindowType.FiveHour, UsageLevel.Danger, "Claude Code", 90, now.AddHours(2).AddMinutes(40), now),
            DemoThreshold(UsageWindowType.Weekly, UsageLevel.Caution, "Codex", 70, now.AddDays(4), now),
            DemoThreshold(UsageWindowType.Weekly, UsageLevel.Danger, "Codex", 90, now.AddDays(1), now),
            DemoReset(UsageWindowType.FiveHour, "Claude Code", 100, now),
            DemoReset(UsageWindowType.Weekly, "Codex", 100, now),
        };

        foreach (var theme in new[] { ElementTheme.Light, ElementTheme.Dark })
        {
            foreach (var sample in samples)
            {
                _displayQueue.Enqueue(new QueuedNotification(
                    sample, theme, TimeSpan.FromSeconds(3), true));
            }
        }
        ShowNext();
    }

    /// <summary>
    /// Developer visual QA: the worst-case (longest) notification text in each of the three
    /// UI languages — title + message — in light and dark, so the fixed-size window can be
    /// checked for clipping. Strings are built per language straight from the resource table,
    /// so one run shows Korean, English, and Japanese regardless of the app's current language.
    /// </summary>
    public void ShowLongestTextDemo()
    {
        var now = DateTimeOffset.Now;
        foreach (var theme in new[] { ElementTheme.Light, ElementTheme.Dark })
        {
            foreach (var lang in new[] { AppLanguage.Korean, AppLanguage.English, AppLanguage.Japanese })
            {
                var (title, message) = LongestText(lang);
                var sample = new UsageNotification
                {
                    Kind = UsageNotificationKind.Threshold,
                    Level = UsageLevel.Danger,
                    ToolName = "Claude Code",
                    WindowType = UsageWindowType.FiveHour,
                    Title = title,
                    Message = message,
                    CreatedAt = now,
                };
                _displayQueue.Enqueue(new QueuedNotification(sample, theme, TimeSpan.FromSeconds(4), true));
            }
        }
        ShowNext();
    }

    /// <summary>
    /// Builds the longest title and message a real notification could show in
    /// <paramref name="lang"/>, by substituting worst-case values (longest tool name and
    /// window label, 100%, a two-digit date) into the actual templates and picking the
    /// longest message variant.
    /// </summary>
    private static (string Title, string Message) LongestText(AppLanguage lang)
    {
        var index = (int)lang;
        var culture = lang.ToCulture();
        string Text(string key) => Strings.Table[key][index] ?? Strings.Table[key][(int)AppLanguage.English]!;

        // The longest window label for this language (e.g. KO "5시간" vs "주간").
        var fiveHour = Text("Label_FiveHour");
        var weekly = Text("Label_Weekly");
        var label = weekly.Length >= fiveHour.Length ? weekly : fiveHour;

        // "Claude Code" is the longest registered tool name; 100% is the widest percent.
        var title = string.Format(culture, Text("Notif_ThresholdTitle"), "Claude Code", label, 100);

        // The widest message that can fill the body, across the notification's variants.
        var date = new DateTime(2026, 12, 31).ToString(Text("DateFormat_MonthDay"), culture);
        var message = new[]
        {
            string.Format(culture, Text("Notif_ResetMessage"), 100.0),
            Text("Reset_Unknown"),
            string.Format(culture, Text("Reset_InDays"), 7, date),
        }.OrderByDescending(s => s.Length).First();

        return (title, message);
    }

    private void ShowNext()
    {
        if (_isShowing || _displayQueue.Count == 0) return;
        if (!_displayQueue.Peek().ForcePresent && !CanPresentNow())
        {
            while (_displayQueue.TryDequeue(out var queued))
            {
                Suppress(queued.Notification);
            }
            return;
        }

        var next = _displayQueue.Dequeue();
        _isShowing = true;
        _window.Show(next.Notification, next.ThemeOverride, next.VisibleDuration);
    }

    private void OnDismissed(object? sender, EventArgs e)
    {
        _isShowing = false;
        ShowNext();
    }

    private void Suppress(UsageNotification notification)
    {
        // Hold only the most recent alert; older ones during Do Not Disturb are stale for a
        // usage gauge, so when DND ends we surface just the latest (no "N missed" count).
        _latestSuppressed = notification;
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
        _displayQueue.Enqueue(new QueuedNotification(_latestSuppressed, null, null, false));
        _latestSuppressed = null;
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

    private static UsageNotification DemoThreshold(
        UsageWindowType windowType, UsageLevel level, string toolName, int percent,
        DateTimeOffset reset, DateTimeOffset now) => new()
    {
        Kind = UsageNotificationKind.Threshold,
        Level = level,
        ToolName = toolName,
        WindowType = windowType,
        Title = NotificationText.ThresholdTitle(toolName, windowType, percent),
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
        Title = NotificationText.ResetTitle(toolName, windowType),
        Message = NotificationText.ResetMessage(availablePercent),
        CreatedAt = now,
    };

    private sealed record QueuedNotification(
        UsageNotification Notification,
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
