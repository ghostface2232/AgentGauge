using System.Runtime.InteropServices;

namespace Gauge.Services;

/// <summary>Seam over the SystemParametersInfo foreground-lock timeout, so the guard's
/// capture/restore protocol is unit-testable without touching the real user setting.</summary>
internal interface IForegroundLockTimeout
{
    bool TryGet(out uint timeout);
    bool TrySet(uint timeout);
}

/// <summary>
/// Zeroes the per-user foreground-lock timeout while Gauge runs — so the tray menu's
/// SecondWindow host can take foreground (see <see cref="TrayIconService"/>) — and
/// restores the user's value on exit. The setting is machine-visible: it changes focus
/// stealing for every app in the login session, so losing the original value matters.
///
/// The baseline is persisted to settings.json before the live value is zeroed, because a
/// hard termination (Task Manager kill, power loss, a native fault that never reaches CLR
/// shutdown) runs no restore path at all: the next launch then reads 0 — Gauge's own
/// leftover — and without the persisted copy it would adopt that 0 as the value to
/// "restore", permanently losing the user's setting after a single hard kill.
///
/// Protocol:
///  - <see cref="Disable"/>: read the live value. A 0 while a persisted baseline exists
///    is treated as a crashed instance's leftover and the persisted value is kept as the
///    baseline. Anything else — a deliberate user 0 included, since a clean exit always
///    clears the key — IS the user's setting and is persisted before the zero is applied.
///  - <see cref="Restore"/>: put the baseline back and clear the persisted key — once, and
///    only once the system has accepted the value (Dispose on the UI thread and the
///    ProcessExit safety net can race here), so the next launch reads the user's
///    then-current value fresh. A failed restore keeps both copies for a later attempt.
///
/// The one accepted trade-off: a user who sets the timeout to 0 by hand between a hard
/// kill and the next launch has that change read as the crash leftover, and the older
/// persisted value wins. Preferring the last value known to be the user's own loses less
/// than the alternative, where every hard kill silently pins the setting at 0 forever.
/// </summary>
internal sealed class ForegroundLockGuard
{
    private readonly IForegroundLockTimeout _timeout;
    private readonly Func<string> _directory;
    private readonly object _gate = new();
    private uint? _baseline;

    public ForegroundLockGuard(IForegroundLockTimeout? timeout = null, Func<string>? directory = null)
    {
        _timeout = timeout ?? new SystemForegroundLockTimeout();
        _directory = directory ?? (() => AppSettingsFile.DefaultDirectory);
    }

    /// <summary>Captures and persists the restore baseline, then zeroes the live timeout.</summary>
    public void Disable()
    {
        if (_timeout.TryGet(out var current))
        {
            // TryLoad, not Load: an unreadable settings.json is indistinguishable from an
            // absent one, and treating it as absent would read a crash-leftover 0 as the
            // user's value and persist it — overwriting the real baseline the file may
            // still hold, which is exactly the permanent loss this class exists to stop.
            if (AppSettingsFile.TryLoad(_directory(), out var settings))
            {
                var persisted = settings.ForegroundLockTimeoutBaseline;
                var baseline = current == 0 && persisted is uint saved ? saved : current;
                lock (_gate)
                {
                    _baseline = baseline;
                }
                if (baseline != persisted)
                {
                    AppSettingsFile.Save(_directory(), dto => dto.ForegroundLockTimeoutBaseline = baseline);
                }
            }
            else if (current != 0)
            {
                // File unreadable but the live value is non-zero: that is unambiguously
                // the user's setting, so keep it in memory for this run's restore. Skip
                // the persist — a transient read failure must never author a write over
                // whatever baseline the file holds.
                lock (_gate)
                {
                    _baseline = current;
                }
            }
            // File unreadable AND live 0: cannot tell a deliberate user 0 from a crash
            // leftover. Adopt nothing — Restore is a no-op this run and the on-disk
            // baseline, if any, survives for the next healthy launch to recover.
        }

        // Applied even when the read failed: an unreadable current value doesn't make the
        // menu need the zero any less, and Restore stays a no-op without a baseline.
        _ = _timeout.TrySet(0);
    }

    /// <summary>
    /// Puts the user's timeout back and clears the persisted baseline — once, on success.
    /// The baseline is taken under the lock so Dispose and the ProcessExit net cannot both
    /// act on it, and put back if the write to the system fails: the live value is then
    /// still Gauge's zero, so dropping the baseline would strand the user's setting with
    /// nothing left to recover it from. Leaving both copies in place lets the ProcessExit
    /// net — or, after a kill, the next launch — try again.
    /// </summary>
    public void Restore()
    {
        uint baseline;
        lock (_gate)
        {
            if (_baseline is not uint saved)
            {
                return;
            }
            baseline = saved;
            _baseline = null;
        }

        if (!_timeout.TrySet(baseline))
        {
            lock (_gate)
            {
                _baseline ??= baseline;
            }
            return;
        }

        // Clear the key only if the file reads cleanly right now and actually holds one.
        // settings.json is shared and TrySave is a read-modify-write whose read fails open,
        // so writing over a file that cannot be parsed would rewrite it from defaults and
        // drop the tool registration, language, view mode, alert flags and any unknown
        // keys. Reading here rather than remembering what Disable wrote also covers the
        // file changing in between, and clears a baseline left behind by an earlier run
        // whose own persist failed.
        if (AppSettingsFile.TryLoad(_directory(), out var settings)
            && settings.ForegroundLockTimeoutBaseline is not null)
        {
            AppSettingsFile.Save(_directory(), dto => dto.ForegroundLockTimeoutBaseline = null);
        }
    }

    private sealed class SystemForegroundLockTimeout : IForegroundLockTimeout
    {
        public bool TryGet(out uint timeout)
        {
            timeout = 0;
            return NativeMethods.SystemParametersInfoGet(
                NativeMethods.SPI_GETFOREGROUNDLOCKTIMEOUT, 0, ref timeout, 0);
        }

        public bool TrySet(uint timeout)
            => NativeMethods.SystemParametersInfoSet(
                NativeMethods.SPI_SETFOREGROUNDLOCKTIMEOUT, 0, (IntPtr)timeout, NativeMethods.SPIF_SENDCHANGE);
    }

    private static class NativeMethods
    {
        public const uint SPI_GETFOREGROUNDLOCKTIMEOUT = 0x2000;
        public const uint SPI_SETFOREGROUNDLOCKTIMEOUT = 0x2001;
        public const uint SPIF_SENDCHANGE = 0x02;

        // GET writes the current timeout into pvParam (a DWORD by reference).
        [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SystemParametersInfoGet(
            uint uiAction, uint uiParam, ref uint pvParam, uint fWinIni);

        // SET passes the new timeout as the pvParam value itself (cast to UINT).
        [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SystemParametersInfoSet(
            uint uiAction, uint uiParam, IntPtr pvParam, uint fWinIni);
    }
}
